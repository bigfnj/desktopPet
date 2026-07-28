# Fortune Packs

Optional add-on fortune collections for **DesktopPet AI Edition**. Each pack is a plain UTF-8
text file, one fortune per line. The pet downloads selected packs (from the in-app **Fortunes**
tab) into `%APPDATA%\DesktopPet\fortunes\`, where they load as new sources you can toggle.

`packs.json` is the manifest the app reads (id, name, description, vibe, count, license, url).

## Provenance & licensing

These are fan-compiled from public/community sources for personal enjoyment, in the long
tradition of the Unix `fortune` program. Content is used as short excerpts:

- **Fair-use / public-domain:** tech, philosophy, literary, facts (fortune-mod datfiles, quotes).
- **Community lists (personal use):** dad jokes & showerthoughts (Reddit), BOFH excuse server.
- **Copyrighted excerpts (personal use):** pop-culture TV dialogue, comedy, spicy.
- **Adults only:** the `nsfw` pack (the classic `fortune -o` set, with hate files removed).

No warranty of accuracy or attribution. If you are a rights holder and want something removed,
open an issue on the repository and it will be taken down.

See `FORTUNE-SOURCES-ASSESSMENT.md` in the repo root for the full source inventory.
