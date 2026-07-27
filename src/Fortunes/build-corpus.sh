#!/usr/bin/env bash
# Build the bundled fortune corpora from a clone of JKirchartz/fortunes (Unlicense/public domain).
#
#   git clone --depth 1 https://github.com/JKirchartz/fortunes.git <srcdir>
#   ./build-corpus.sh <srcdir>
#
# Produces (next to this script), classic BSD fortune format (entries separated by a "%" line):
#   fortunes-sfw.txt    curated family-friendly collections, ENTRY-level profanity-filtered
#   fortunes-spicy.txt  everything (SFW set + edgy collections), UNFILTERED (opt-in)
#
# Each entry is normalized to a single line (fits a pet speech bubble) and length-gated to 8..280 chars.
set -euo pipefail
SRC="${1:?usage: build-corpus.sh <clone-of-JKirchartz-fortunes>}"
OUT="$(cd "$(dirname "$0")" && pwd)"

SFW_FILES="classic_philosophy modern_philosophy authors artists tao montaigne HeraclitusFragments \
SimoneWeil jung Gurdjieff mencken wblake ogden_nash stevenson korzybski Paine Rousseau Bakunin \
Kerouac-Modern-Prose brecht_dances-events-puzzles haraway bruno-latour immortal_consciousness \
existentialriddles Twenty_Lessons_On_Tyranny friedman_12-structures Schlesinger invisiblestates \
predictions MrRogers ObliqueStrategies epigrams_in_programming lwall-quotes hackers hacker-questions \
ComputerDictionary rfc1925 enkiv2s-glossary-of-tech-industry-terms rhetorical-devices anathem-glossary \
ObscureSorrows EnglishAsSheIsSpoke SimpsonsChalkboard FerengiRulesOfAcquisition redgreen handey groucho \
pirate SeventyMaximsOfMaximallyEffectiveMercenaries actualcookies realfacts godin entertainers AClaude \
racter critics Jenny_Holzer activists Andromeda PA-historical-markers"

SPICY_EXTRA="yo-mama carlin chuckfacts subgenius RAW showerthoughts BibleAbridged conalnet higgins_metadramas"

PROFANITY='\b(fuck\w*|shit\w*|cunt\w*|bitch\w*|dick|cock|pussy|asshole|ass|damn|bastard|piss\w*|penis|vagina|nigg\w*|fag\w*|retard\w*|whore|slut|rape|porn|boob\w*|tits?|sex|semen|jizz|masturbat\w*)\b'

# Parse BSD fortune files -> one normalized entry per output line.
parse() {
  for f in "$@"; do [ -f "$SRC/$f" ] && { cat "$SRC/$f"; printf '\n%%\n'; }; done \
  | awk 'BEGIN{e=""}
         /^%[ \t\r]*$/ { if (e!="") print e; e=""; next }
         { l=$0; gsub(/\r/,"",l); e=(e=="")?l:(e" "l) }
         END { if (e!="") print e }' \
  | sed -E 's/[[:space:]]+/ /g; s/^ +//; s/ +$//' \
  | awk 'length>=8 && length<=280'
}

parse $SFW_FILES              | grep -viE "$PROFANITY" | sort -u | awk '{print; print "%"}' > "$OUT/fortunes-sfw.txt"
parse $SFW_FILES $SPICY_EXTRA |                          sort -u | awk '{print; print "%"}' > "$OUT/fortunes-spicy.txt"

echo "SFW   entries: $(grep -c '^%$' "$OUT/fortunes-sfw.txt")   ($(wc -c < "$OUT/fortunes-sfw.txt") bytes)"
echo "Spicy entries: $(grep -c '^%$' "$OUT/fortunes-spicy.txt")   ($(wc -c < "$OUT/fortunes-spicy.txt") bytes)"
