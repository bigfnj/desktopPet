#!/usr/bin/env bash
# Apply labels-store.tsv (text -> topic,genre) to the embedded corpus + every pack, rewriting them
# to the new schema: source <TAB> topic <TAB> genre <TAB> level <TAB> prof <TAB> text.
# Writes <file>.new next to each; review the unmatched counts, then move them into place.
set -eu
FORT="/d/.claude/projects/desktopPet/src/Fortunes"; ROOT="/d/.claude/projects/desktopPet"; STORE="$FORT/labels-store.tsv"

# GATE 1 (schema safety): this rewrites files to the 6-column schema
#   source <TAB> topic <TAB> genre <TAB> level <TAB> prof <TAB> text
# The running app's FortuneProvider still parses 5 columns. Applying + swapping the .new files
# into place BEFORE the parser is updated to 6 columns would misread genre->level, level->prof,
# and prefix spoken text with a stray field. Refuse unless explicitly forced.
if [ "${1:-}" != "--go" ]; then
  echo "GATED: label-apply emits the 6-column schema. FIRST update FortuneProvider (LoadEmbedded/"
  echo "LoadCustom + Sources) to parse 6 columns, THEN re-run:  ./label-apply.sh --go"
  exit 2
fi
# GATE 2 (completeness): never apply a partial classification.
uniq=$(wc -l < "$FORT/label-input.tsv"); store=$([ -f "$STORE" ] && wc -l < "$STORE" || echo 0)
if [ "$store" -lt "$uniq" ]; then
  echo "GATED: store incomplete ($store/$uniq labeled). Finish the pass (label-merge must report COMPLETE) first."
  exit 3
fi

apply() {
  local f="$1"
  awk -F'\t' -v store="$STORE" '
    BEGIN{ while((getline l < store)>0){ n=split(l,a,"\t"); T[a[1]]=a[2]; G[a[1]]=a[3]; S[a[1]]=1 } }
    NF>=5 { tx=$NF; tp=(tx in T)?T[tx]:"life"; gn=(tx in G)?G[tx]:"quip";
            if(!(tx in S)) miss++;
            print $1"\t"tp"\t"gn"\t"$3"\t"$4"\t"$NF }
    END{ printf "" > "/dev/stderr" }
  ' "$f" > "$f.new"
  local miss; miss=$(awk -F'\t' -v store="$STORE" 'BEGIN{while((getline l<store)>0){split(l,a,"\t");S[a[1]]=1}} NF>=5 && !($NF in S){c++} END{print c+0}' "$f")
  printf "  %-22s %6d lines  unmatched=%d\n" "$(basename "$f")" "$(wc -l < "$f.new")" "$miss"
}
echo "applying labels ($(wc -l < "$STORE") in store)..."
apply "$FORT/fortunes.txt"
for p in "$ROOT"/packs/*.txt; do apply "$p"; done
echo "wrote .new files. Review unmatched, then: for f in fortunes.txt packs/*.txt; do mv \$f.new \$f; done"