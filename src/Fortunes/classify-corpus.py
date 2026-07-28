#!/usr/bin/env python3
"""Tag each corpus line with a content level and a profanity flag.

Reads a tab-separated corpus whose LAST field is the fortune text and whose FIRST field is
the source collection, and rewrites it in place as:

    source<TAB>category<TAB>level<TAB>prof<TAB>text

    level = general | edgy | nsfw
      nsfw    -> explicit sexual / graphic language
      edgy    -> profanity or crude/offensive humor (incl. inherently-adult sources)
      general -> everything else
    prof  = 1 if the text contains any profanity, else 0

Idempotent: existing level/prof columns are ignored and recomputed from the text. Any
leading columns between source and text (e.g. category) are preserved.
Read/written with surrogateescape so the corpus's stray non-UTF-8 byte round-trips.
"""
import re, sys

# Explicit sexual / graphic -> nsfw.
NSFW = re.compile(r"\b("
    r"pussy|cocks|dicks|penis|penises|vagina\w*|cums|cumming|jizz|"
    r"blow ?jobs?|hand ?jobs?|rim ?jobs?|masturbat\w*|porn\w*|rape|raping|rapist|"
    r"dildo\w*|orgasms?|semen|ejaculat\w*|horny|clit\w*|nipples?|"
    r"titties|titty|slut\w*|whore\w*|cunt\w*|nsfw|hentai|dominatrix|"
    r"fetish\w*|genital\w*|scrotum|testicles?|foreskin|blowie|creampie|cumshot|"
    r"deepthroat|felch\w*|fisting|gangbang|bukkake|blow job|handjob"
    r")\b", re.IGNORECASE)

# Profanity that is crude but not explicitly sexual -> edgy.
EDGY = re.compile(r"\b("
    r"fuck\w*|shit\w*|bitch\w*|asshole\w*|ass|arse\w*|damn|goddamn\w*|bastard\w*|"
    r"piss\w*|nigg\w*|fag|faggot\w*|retard\w*|douche\w*|pricks?|wank\w*|bollocks|"
    r"twat\w*|jackass|dumbass|motherfuck\w*|bullshit|dick|cock|boob\w*|tits"
    r")\b", re.IGNORECASE)

# Sources whose humor is inherently adult even when the wording is clean.
EDGY_SOURCES = {"yo-mama", "carlin"}

def classify(text, source):
    prof = 1 if (NSFW.search(text) or EDGY.search(text)) else 0
    if NSFW.search(text):
        level = "nsfw"
    elif EDGY.search(text) or source in EDGY_SOURCES:
        level = "edgy"
    else:
        level = "general"
    return level, prof

def process(path):
    out = []
    counts = {"general": 0, "edgy": 0, "nsfw": 0}
    prof_n = 0
    for ln in open(path, encoding='utf-8', errors='surrogateescape'):
        parts = ln.rstrip('\n').split('\t')
        if len(parts) < 3:
            out.append(ln.rstrip('\n')); continue
        source = parts[0]
        text = parts[-1]
        # Preserve any middle columns (category) but drop a prior level/prof so we recompute.
        # Layouts handled: [source, category, text] or [source, category, level, prof, text].
        category = parts[1]
        level, prof = classify(text, source)
        out.append('\t'.join([source, category, level, str(prof), text]))
        counts[level] += 1
        prof_n += prof
    with open(path, 'w', encoding='utf-8', errors='surrogateescape', newline='\n') as w:
        w.write('\n'.join(out) + '\n')
    print(f"{path}: general={counts['general']} edgy={counts['edgy']} nsfw={counts['nsfw']} "
          f"profane={prof_n} total={len(out)}")

if __name__ == '__main__':
    for f in (sys.argv[1:] or ['fortunes.txt']):
        process(f)
