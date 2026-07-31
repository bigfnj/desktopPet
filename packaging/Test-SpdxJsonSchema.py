#!/usr/bin/env python3
"""Offline validation against the repository-pinned official SPDX 2.3 schema."""

from __future__ import annotations

import argparse
import base64
import gzip
import hashlib
import importlib.metadata
import json
from pathlib import Path
import sys


EXPECTED_SCHEMA_SHA256 = (
    "239208b7ac287b3cf5d9a9af23f9d69863971102a5e1587a27a398b43490b89b"
)
EXPECTED_JSONSCHEMA_VERSION = "4.26.0"


def load_json_strict(path: Path) -> object:
    try:
        text = path.read_bytes().decode("utf-8", errors="strict")
    except UnicodeDecodeError as exc:
        raise ValueError(f"{path}: invalid UTF-8 at byte {exc.start}") from exc
    try:
        return json.loads(text)
    except json.JSONDecodeError as exc:
        raise ValueError(
            f"{path}: invalid JSON at line {exc.lineno}, column {exc.colno}: "
            f"{exc.msg}"
        ) from exc


def load_pinned_schema(path: Path) -> object:
    try:
        compressed = base64.b64decode(
            path.read_text(encoding="ascii").strip(), validate=True
        )
        raw = gzip.decompress(compressed)
    except (OSError, UnicodeError, ValueError) as exc:
        raise ValueError(f"{path}: invalid vendored schema payload: {exc}") from exc
    actual_hash = hashlib.sha256(raw).hexdigest()
    if actual_hash != EXPECTED_SCHEMA_SHA256:
        raise ValueError(
            f"{path}: official SPDX schema SHA-256 mismatch: {actual_hash}"
        )
    try:
        return json.loads(raw.decode("utf-8", errors="strict"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"{path}: decoded schema is invalid: {exc}") from exc


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("sbom", type=Path)
    parser.add_argument(
        "--schema",
        type=Path,
        default=Path(__file__).with_name(
            "spdx-2.3.schema.json.gz.base64"
        ),
    )
    args = parser.parse_args()

    try:
        installed_version = importlib.metadata.version("jsonschema")
    except importlib.metadata.PackageNotFoundError:
        print(
            "jsonschema 4.26.0 is required for offline SPDX validation.",
            file=sys.stderr,
        )
        return 2
    if installed_version != EXPECTED_JSONSCHEMA_VERSION:
        print(
            "Expected jsonschema "
            f"{EXPECTED_JSONSCHEMA_VERSION}; found {installed_version}.",
            file=sys.stderr,
        )
        return 2

    import jsonschema

    try:
        schema = load_pinned_schema(args.schema.resolve())
        document = load_json_strict(args.sbom.resolve())
        jsonschema.Draft7Validator.check_schema(schema)
        validator = jsonschema.Draft7Validator(
            schema,
        )
        errors = sorted(
            validator.iter_errors(document),
            key=lambda item: [str(part) for part in item.absolute_path],
        )
    except (OSError, ValueError, jsonschema.SchemaError) as exc:
        print(str(exc), file=sys.stderr)
        return 2

    if errors:
        for error in errors[:25]:
            location = "$"
            for part in error.absolute_path:
                location += (
                    f"[{part}]" if isinstance(part, int) else f".{part}"
                )
            print(f"{location}: {error.message}", file=sys.stderr)
        if len(errors) > 25:
            print(
                f"... {len(errors) - 25} additional schema errors",
                file=sys.stderr,
            )
        return 1

    print(
        "SPDX 2.3 official JSON schema validation: PASS "
        f"({args.sbom.resolve()})"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
