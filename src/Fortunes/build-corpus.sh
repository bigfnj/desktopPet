#!/usr/bin/env bash
# Build the bundled fortune corpora from a clone of JKirchartz/fortunes (Unlicense/public domain).
#
#   git clone --depth 1 https://github.com/JKirchartz/fortunes.git <srcdir>
#   ./build-corpus.sh <srcdir>
#
# Output: one fortune per line, "category<TAB>rating<TAB>text".
#   category = coarse topic for Phase B contextual routing
#              (tech / wisdom / creative / whimsy / facts / work / observations / general)
#   rating   = sfw | spicy   (spicy = profanity/NSFW hit, or from an inherently-adult source)
#   text     = the fortune, normalized to a single bubble-sized line (8..280 chars)
#
#   fortunes-sfw.txt    curated family-friendly collections, profanity-filtered (all rating=sfw)
#   fortunes-spicy.txt  everything (SFW set + edgy collections), each line rated sfw/spicy (opt-in)
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

# Inherently-adult sources: every line is rated spicy even if it doesn't trip the wordlist.
ADULT_FILES="yo-mama carlin"

PROFANITY='\b(fuck\w*|shit\w*|cunt\w*|bitch\w*|dick|cock|pussy|asshole|ass|damn|bastard|piss\w*|penis|vagina|nigg\w*|fag\w*|retard\w*|whore|slut|rape|porn|boob\w*|tits?|sex|semen|jizz|masturbat\w*)\b'

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

is_adult() { case " $ADULT_FILES " in *" $1 "*) return 0 ;; *) return 1 ;; esac; }

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

# SFW: drop profane entries, tag rating=sfw.  (|| true: grep exits 1 when nothing is filtered.)
emit_sfw() {
  local f="$1" c; c="$(catof "$f")"
  parse_entries "$f" | { grep -viE "$PROFANITY" || true; } | awk -v c="$c" '{print c "\tsfw\t" $0}'
}

# Spicy: keep everything; rating=spicy when the entry hits the wordlist or comes from an adult source.
emit_spicy() {
  local f="$1" c; c="$(catof "$f")"
  local ents; ents="$(mktemp)"; parse_entries "$f" > "$ents"
  if is_adult "$f"; then
    awk -v c="$c" '{print c "\tspicy\t" $0}' "$ents"
  else
    local prof; prof="$(mktemp)"
    { grep -iE "$PROFANITY" "$ents" || true; } | LC_ALL=C sort -u > "$prof"   # exits 1 when clean
    awk -v c="$c" 'NR==FNR{p[$0]=1;next}{ r=($0 in p)?"spicy":"sfw"; print c "\t" r "\t" $0 }' "$prof" "$ents"
    rm -f "$prof"
  fi
  rm -f "$ents"
}

build() {   # $1=outfile  $2=sfw|spicy  rest=files
  local out="$1" mode="$2"; shift 2
  local tmp; tmp="$(mktemp)"; local f
  for f in "$@"; do "emit_$mode" "$f" >> "$tmp"; done
  awk -F'\t' '!seen[$3]++' "$tmp" | LC_ALL=C sort > "$out"   # dedupe on text, stable order
  rm -f "$tmp"
}

build "$OUT/fortunes-sfw.txt"   sfw   $SFW_FILES
build "$OUT/fortunes-spicy.txt" spicy $SFW_FILES $SPICY_EXTRA

echo "SFW   entries: $(wc -l < "$OUT/fortunes-sfw.txt")   ($(wc -c < "$OUT/fortunes-sfw.txt") bytes)"
echo "Spicy entries: $(wc -l < "$OUT/fortunes-spicy.txt")   ($(wc -c < "$OUT/fortunes-spicy.txt") bytes)"
echo "Spicy ratings:"; cut -f2 "$OUT/fortunes-spicy.txt" | LC_ALL=C sort | uniq -c
echo "Spicy categories:"; cut -f1 "$OUT/fortunes-spicy.txt" | LC_ALL=C sort | uniq -c | sort -rn