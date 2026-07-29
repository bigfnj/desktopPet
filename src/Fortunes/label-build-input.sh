#!/usr/bin/env bash
# Build the frozen labeling input for the FULL pass: unique fortune texts across the embedded
# corpus + every pack, shuffled. Store is keyed by text so labels apply back to any file that
# contains that fortune, and the grind resumes cleanly (label-next skips texts already in store).
set -eu
FORT="/d/.claude/projects/desktopPet/src/Fortunes"
ROOT="/d/.claude/projects/desktopPet"
IN="$FORT/label-input.tsv"        # source-hint <TAB> text   (unique, shuffled, frozen)
STORE="$FORT/labels-store.tsv"    # text <TAB> topic <TAB> genre
{
  awk -F'\t' 'NF>=5{print "embedded\t"$NF}' "$FORT/fortunes.txt"
  for p in "$ROOT"/packs/*.txt; do
    b=$(basename "$p" .txt)
    awk -F'\t' -v s="$b" 'NF>=5{print s"\t"$NF}' "$p"
  done
} | awk -F'\t' '!seen[$2]++' | shuf > "$IN"
[ -f "$STORE" ] || : > "$STORE"
echo "unique fortunes to label: $(wc -l < "$IN")   (already labeled: $(wc -l < "$STORE"))"