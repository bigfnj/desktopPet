#!/usr/bin/env bash
# Validate + append this batch's labels. label-batch.txt = one "topic genre" per line, in the
# exact order label-next.sh printed .batchtexts.
set -eu
FORT="/d/.claude/projects/desktopPet/src/Fortunes"
STORE="$FORT/labels-store.tsv"; BT="$FORT/.batchtexts"; BATCH="$FORT/label-batch.txt"
exp=$(grep -c . "$BT"); got=$(grep -c . "$BATCH")
[ "$got" -eq "$exp" ] || { echo "COUNT MISMATCH expected=$exp got=$got — NOT ingesting"; exit 1; }
bad=$(awk 'NF!=2{c++} END{print c+0}' "$BATCH")
[ "$bad" -eq 0 ] || { echo "$bad line(s) not exactly 'topic genre' — NOT ingesting"; exit 1; }
paste "$BT" "$BATCH" | awk -F'\t' '{split($2,a," "); print $1"\t"a[1]"\t"a[2]}' >> "$STORE"
echo "ingested=$got  total=$(wc -l < "$STORE")/$(wc -l < "$FORT/label-input.tsv")"
: > "$BATCH"; : > "$BT"