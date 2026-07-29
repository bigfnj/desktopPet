#!/usr/bin/env bash
# Print the next unlabeled batch of the frozen embedded corpus for hand-labeling.
#   label-input.tsv  = frozen shuffled snapshot: source<TAB>level<TAB>prof<TAB>text (order-locked)
#   labels-embedded.tsv = appended one "topic<TAB>genre" per input line, in order
# Usage: ./label-next.sh [batchsize]   (default 300)
set -eu
FORT="/d/.claude/projects/desktopPet/src/Fortunes"
IN="$FORT/label-input.tsv"
LBL="$FORT/labels-embedded.tsv"
B="${1:-300}"
done=$([ -f "$LBL" ] && wc -l < "$LBL" || echo 0)
total=$(wc -l < "$IN")
start=$((done+1)); end=$((done+B)); [ "$end" -gt "$total" ] && end=$total
printf 'start=%s end=%s total=%s\n' "$start" "$end" "$total" > "$FORT/.batchmeta"
if [ "$start" -gt "$total" ]; then echo "### ALL $total LINES LABELED — run apply. ###"; exit 0; fi
echo "### BATCH: input lines $start..$end of $total  (emit EXACTLY $((end-start+1)) 'topic genre' lines, IN ORDER) ###"
awk -F'\t' -v s="$start" -v e="$end" 'NR>=s&&NR<=e{printf "%d| (%s) %s\n", NR, $1, $4}' "$IN"