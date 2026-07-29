#!/usr/bin/env bash
# Validate + append the batch labels I wrote to label-batch.txt (one "topic genre" per line, in order).
set -eu
FORT="/d/.claude/projects/desktopPet/src/Fortunes"
LBL="$FORT/labels-embedded.tsv"; BATCH="$FORT/label-batch.txt"
exp=$(awk '{for(i=1;i<=NF;i++){split($i,a,"=");m[a[1]]=a[2]}} END{print m["end"]-m["start"]+1}' "$FORT/.batchmeta")
got=$(grep -c . "$BATCH")
[ "$got" -eq "$exp" ] || { echo "COUNT MISMATCH expected=$exp got=$got — NOT ingesting"; exit 1; }
bad=$(awk 'NF!=2{c++} END{print c+0}' "$BATCH")
[ "$bad" -eq 0 ] || { echo "$bad line(s) are not exactly 'topic genre' — NOT ingesting"; exit 1; }
awk '{print $1"\t"$2}' "$BATCH" >> "$LBL"
echo "ingested=$got  total_labeled=$(wc -l < "$LBL")/$(wc -l < "$FORT/label-input.tsv")"
: > "$BATCH"