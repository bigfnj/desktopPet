#!/usr/bin/env bash
# Build the bundled fortune corpus from a clone of JKirchartz/fortunes (Unlicense/public domain).
#
#   git clone --depth 1 https://github.com/JKirchartz/fortunes.git <srcdir>
#   ./build-corpus.sh <srcdir>
#
# Output: one fortune per line in fortunes.txt, tab-separated:
#   source<TAB>category<TAB>level<TAB>prof<TAB>text
#     source   = the origin collection (e.g. SimpsonsChalkboard) — powers the per-source picker
#     category = coarse topic for Phase B contextual routing
#                (tech / wisdom / creative / whimsy / facts / work / observations / general)
#     level    = general | edgy | nsfw   — content severity (classify-corpus.py)
#     prof     = 1 if the text contains profanity, else 0
#     text     = the fortune, normalized to a single bubble-sized line (8..280 chars)
#
# The single tagged file lets FortuneProvider filter everything at runtime (spicy tier,
# remove-profanity, and per-source selection) instead of shipping pre-split sfw/spicy files.
set -euo pipefail
SRC="${1:?usage: build-corpus.sh <clone-of-JKirchartz-fortunes>}"
OUT="$(cd "$(dirname "$0")" && pwd)"

# The curated collections we ship. (Order only affects which source keeps a cross-source
# duplicate.) New collections can be added here; the picker discovers them automatically.
ALL_FILES="classic_philosophy modern_philosophy authors artists tao montaigne HeraclitusFragments \
SimoneWeil jung Gurdjieff mencken wblake ogden_nash stevenson korzybski Paine Rousseau Bakunin \
Kerouac-Modern-Prose brecht_dances-events-puzzles haraway bruno-latour immortal_consciousness \
existentialriddles Twenty_Lessons_On_Tyranny friedman_12-structures Schlesinger invisiblestates \
predictions MrRogers ObliqueStrategies epigrams_in_programming lwall-quotes hackers hacker-questions \
ComputerDictionary rfc1925 enkiv2s-glossary-of-tech-industry-terms rhetorical-devices anathem-glossary \
ObscureSorrows EnglishAsSheIsSpoke SimpsonsChalkboard FerengiRulesOfAcquisition redgreen handey groucho \
pirate SeventyMaximsOfMaximallyEffectiveMercenaries actualcookies realfacts godin entertainers AClaude \
racter critics Jenny_Holzer activists Andromeda PA-historical-markers \
yo-mama carlin chuckfacts subgenius RAW showerthoughts BibleAbridged conalnet higgins_metadramas"

catof() {
  case "$1" in
    epigrams_in_programming|hackers|hacker-questions|lwall-quotes|ComputerDictionary|rfc1925|enkiv2s-glossary-of-tech-industry-terms) echo tech ;;
    classic_philosophy|modern_philosophy|tao|montaigne|HeraclitusFragments|SimoneWeil|jung|Gurdjieff|mencken|korzybski|Paine|Rousseau|Bakunin|immortal_consciousness|existentialriddles|friedman_12-structures|Twenty_Lessons_On_Tyranny|bruno-latour|haraway|Schlesinger|invisiblestates|predictions|brecht_dances-events-puzzles|Kerouac-Modern-Prose|subgenius|RAW|BibleAbridged|higgins_metadramas) echo wisdom ;;
    authors|artists|wblake|ogden_nash|stevenson|Jenny_Holzer|ObliqueStrategies|ObscureSorrows|rhetorical-devices|anathem-glossary|EnglishAsSheIsSpoke|racter|critics) echo creative ;;
    MrRogers|handey|groucho|pirate|SimpsonsChalkboard|FerengiRulesOfAcquisition|redgreen|SeventyMaximsOfMaximallyEffectiveMercenaries|actualcookies|entertainers|AClaude|Andromeda|carlin|chuckfacts|yo-mama|conalnet) echo whimsy ;;
    realfacts|PA-historical-markers) echo facts ;;
    godin|activists) echo work ;;
    showerthoughts) echo observations ;;
    *) echo general ;;
  esac
}

# Normalized entries (one per line, no tags, no filtering) from one BSD fortune file.
parse_entries() {
  local f="$1"
  [ -f "$SRC/$f" ] || return 0
  { cat "$SRC/$f"; printf '\n%%\n'; } \
  | awk 'BEGIN{e=""}
         /^%[ \t\r]*$/ { if (e!="") print e; e=""; next }
         { l=$0; gsub(/\r/,"",l); e=(e=="")?l:(e" "l) }
         END { if (e!="") print e }' \
  | sed -E 's/[[:space:]]+/ /g; s/^ +//; s/ +$//' \
  | awk 'length>=8 && length<=280'
}

# source<TAB>category<TAB>text  (level/prof are added later by classify-corpus.py)
emit() {
  local f="$1" c; c="$(catof "$f")"
  parse_entries "$f" | awk -v src="$f" -v c="$c" '{print src "\t" c "\t" $0}'
}

tmp="$(mktemp)"
for f in $ALL_FILES; do emit "$f" >> "$tmp"; done
awk -F'\t' '!seen[$3]++' "$tmp" | LC_ALL=C sort > "$OUT/fortunes.txt"   # dedupe on text, stable order
rm -f "$tmp"

# Strip trailing author/attribution bylines (the pet does not speak "-- Neil Gaiman" tags).
python "$(dirname "$0")/strip-authors.py"  "$OUT/fortunes.txt"
# Tag each line with content level (general/edgy/nsfw) + a profanity flag.
python "$(dirname "$0")/classify-corpus.py" "$OUT/fortunes.txt"

echo "Total entries: $(wc -l < "$OUT/fortunes.txt")   ($(wc -c < "$OUT/fortunes.txt") bytes)"
echo "Levels:";     cut -f3 "$OUT/fortunes.txt" | LC_ALL=C sort | uniq -c
echo "Profanity:";  cut -f4 "$OUT/fortunes.txt" | LC_ALL=C sort | uniq -c
echo "Categories:"; cut -f2 "$OUT/fortunes.txt" | LC_ALL=C sort | uniq -c | sort -rn
echo "Sources:";    cut -f1 "$OUT/fortunes.txt" | LC_ALL=C sort | uniq -c | sort -rn