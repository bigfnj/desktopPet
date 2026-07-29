#!/usr/bin/env bash
# Print the next batch of still-unlabeled fortunes (text-keyed; resumable).
# Usage: ./label-next.sh [batchsize]   (default 500)
set -eu
FORT="/d/.claude/projects/desktopPet/src/Fortunes"
IN="$FORT/label-input.tsv"; STORE="$FORT/labels-store.tsv"; BT="$FORT/.batchtexts"
B="${1:-500}"
[ -f "$STORE" ] || : > "$STORE"
# input rows whose text (col2) is not yet a key in store (col1); take the first B
awk -F'\t' 'NR==FNR{done[$1]=1;next} !($2 in done){print $2}' "$STORE" "$IN" | head -n "$B" > "$BT"
n=$(grep -c . "$BT" || echo 0)
labeled=$(wc -l < "$STORE"); total=$(wc -l < "$IN")
if [ "$n" -eq 0 ]; then echo "### ALL $total LABELED. Run label-apply.sh. ###"; exit 0; fi
echo "### BATCH ready: $n lines written to .batchtexts (progress $labeled/$total). Read it, emit EXACTLY $n 'topic genre' lines IN ORDER. ###"