#!/usr/bin/env bash
# Rebuild labels-store.tsv from all valid chunk .out files. Validates each chunk (count, tokens,
# ordering) and reports which need a re-run. Idempotent: safe to re-run after re-dispatching.
set -eu
FORT="/d/.claude/projects/desktopPet/src/Fortunes"; CH="$FORT/label-chunks"; STORE="$FORT/labels-store.tsv"
VT="|tech|science|work-money|love|family|faith|society|food|nature|arts|life|"
VG="|tv-quote|observation|joke|pun|quip|aphorism|wisdom|fact|insult|verse|dark|uplifting|"
: > "$STORE"
pass=0; fail=0; miss=0; failed=""
for tsv in "$CH"/chunk*.tsv; do
  nn=$(basename "$tsv" .tsv); out="$CH/$nn.out"; exp=$(wc -l < "$tsv")
  if [ ! -f "$out" ]; then miss=$((miss+1)); failed="$failed $nn(missing)"; continue; fi
  got=$(grep -c . "$out")
  if [ "$got" -ne "$exp" ]; then fail=$((fail+1)); failed="$failed $nn(count=$got/$exp)"; continue; fi
  bad=$(awk -v vt="$VT" -v vg="$VG" 'NF!=3||index(vt,"|"$2"|")==0||index(vg,"|"$3"|")==0{c++} END{print c+0}' "$out")
  ord=$(awk '{if($1+0!=FNR)c++} END{print c+0}' "$out")
  if [ "$bad" -ne 0 ] || [ "$ord" -ne 0 ]; then fail=$((fail+1)); failed="$failed $nn(bad=$bad,ord=$ord)"; continue; fi
  paste <(cut -f2 "$tsv") <(awk '{print $2"\t"$3}' "$out") >> "$STORE"
  pass=$((pass+1))
done
echo "chunks: passed=$pass failed=$fail missing=$miss"
[ -n "$failed" ] && echo "NEEDS REDO:$failed"
echo "store: $(wc -l < "$STORE") / $(wc -l < "$FORT/label-input.tsv") labeled"
# The store is rebuilt from valid chunks for progress visibility, but signal INCOMPLETE via a
# non-zero exit so label-apply (or any caller) never proceeds on a partial classification.
if [ "$fail" -gt 0 ] || [ "$miss" -gt 0 ]; then
  echo "INCOMPLETE: re-run the flagged chunks before applying."; exit 1
fi
echo "COMPLETE: all chunks valid."