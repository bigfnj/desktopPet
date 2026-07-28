#!/usr/bin/env python3
"""Strip trailing author/attribution bylines from the bundled fortune corpus.

The upstream fortune sources tag most entries with a byline the desktop pet does
not want to speak, in a handful of shapes:

    quote -- Ogden Nash                     (BSD 'fortune' double-dash delimiter)
    quote -- Larry Wall in <msgid>          (name + source clause)
    quote ---User, Mon YYYY   (U+2015 '-')   (reddit 'showerthoughts' marker)
    quote - Richard Feynman                 (single dash + 'First Last')
    quote. - Seth Godin                     (single dash after sentence end)
    quote -- Author, The Book Title -- ...   (chained double bylines)

It is deliberately high-precision: a dash is only treated as a byline when the
tail actually looks like a name, so prose dashes inside quotes (Perlis epigrams,
Red Green dialogue, Le Guin, '-- Yoko and I've never changed') are preserved.
A few authors are appended with no separator at all (', Ursula K. Le Guin') and
are left as-is — they are indistinguishable from ordinary sentence-final nouns.

Idempotent. Operates in place on the files given (default: both corpus files).
Files are read/written with surrogateescape so the corpus's stray non-UTF-8
byte round-trips untouched.
"""
import re, sys

HB = u'―'   # HORIZONTAL BAR  — the reddit-showerthoughts byline marker
EM = u'—'   # EM DASH
EN = u'–'   # EN DASH
CAP = set("ABCDEFGHIJKLMNOPQRSTUVWXYZ\"'" + u'“‘”')
PARTICLE = {'de','van','von','der','den','la','le','du','di','da','del','della',
            'the','of','and','dos','das','y','ibn','al','st.','st'}

def _name_token(w):
    if re.fullmatch(r"[A-Z][A-Za-z.'’\-]*", w): return True       # Capitalized / initials
    if re.fullmatch(r"[0-9]{1,4}(-[0-9]{2,4})?", w): return True        # volume / year e.g. 1 1774-79
    if w.lower() in PARTICLE: return True                              # name / title particles
    if w in ('&', '.'): return True
    return False

def looks_like_attr(tail):
    """Comma-aware name test: the author segment (before the first comma) must be
    1-5 Title-Case name tokens. Separates real bylines from prose clauses."""
    tail = tail.strip().rstrip('.')
    if not tail or len(tail) > 60: return False
    if re.search(r'[!?]', tail): return False
    author = tail.split(',')[0].strip()
    words = author.split()
    if not (1 <= len(words) <= 5): return False
    if not author[0].isupper(): return False
    return all(_name_token(w) for w in words)

def dd_attr(tail):
    """Looser acceptor for the strongly-attributive ' -- ' delimiter: a single
    capitalized mononym, or 'First Last...' (two leading Capitalized tokens, which
    lets a source clause follow, e.g. 'Larry Wall in <msgid>'). 'Cap lowercase...'
    is prose ('-- Yoko and I've...') and is rejected."""
    tail = tail.strip()
    if not tail or not tail[0].isupper(): return False
    toks = tail.split()
    if len(toks) >= 2 and toks[0][0].isupper() and toks[1][0].isupper(): return True
    if re.fullmatch(r"[A-Z][\w.'’-]{1,24}[.,;:]?", tail): return True
    return False

def two_token_name(tail):
    """Strong 'First Last' byline for the single-dash rule — excludes bare mononyms
    (too risky after a single dash, e.g. 'Call me - Ishmael')."""
    tail = tail.strip()
    if len(tail) > 45 or re.search(r'[!?]', tail): return False
    toks = tail.split(',')[0].split()
    if len(toks) < 2: return False
    return all(_name_token(w) for w in toks[:4]) and toks[0][0].isupper() and toks[1][0].isupper()

_TP = re.escape('.!?"' + u'”’')

def _strip_once(t):
    m = re.match(r'^(.*\S)\s+-{2,}\s+(\S.*)$', t)                 # ' -- Author'
    if m and (looks_like_attr(m.group(2)) or dd_attr(m.group(2))): return m.group(1), True
    m = re.match(r'^(.*\S)\s+[' + EM + EN + r']\s+(\S.*)$', t)    # ' -- Author' / ' - Author' (unicode dash)
    if m and (looks_like_attr(m.group(2)) or dd_attr(m.group(2))): return m.group(1), True
    m = re.match(r'^(.*[' + _TP + r'])\s+-\s+(\S.*)$', t)         # '. - Author' (after sentence end)
    if m and looks_like_attr(m.group(2)): return m.group(1), True
    m = re.match(r'^(.*\S)\s+-\s+(\S.*)$', t)                     # ' - First Last'
    if m and two_token_name(m.group(2)): return m.group(1), True
    return t, False

def strip_author(t):
    o = t
    if HB in t: t = t[:t.index(HB)]      # reddit marker: cut from first (never appears mid-prose)
    for _ in range(4):                   # loop to unwind chained ' -- X -- Y' bylines
        t, chg = _strip_once(t)
        if not chg: break
    t = re.sub(r'[\s,;:\-' + EM + EN + HB + r']+$', '', t).strip()
    return t

def process(path):
    # Text is always the LAST tab-separated field; any leading tag columns are preserved
    # as-is (works for source<TAB>category<TAB>text and older 2-tag layouts alike).
    out = []
    changed = dropped = 0
    for ln in open(path, encoding='utf-8', errors='surrogateescape'):
        parts = ln.rstrip('\n').split('\t')
        if len(parts) < 2:
            out.append(ln.rstrip('\n')); continue
        lead, text = parts[:-1], parts[-1]
        new = strip_author(text)
        if new != text: changed += 1
        if len(new) < 8: dropped += 1; continue          # author-only fragment: drop
        out.append('\t'.join(lead + [new]))
    with open(path, 'w', encoding='utf-8', errors='surrogateescape', newline='\n') as w:
        w.write('\n'.join(out) + '\n')
    print(f"{path}: bylines stripped={changed} short-fragments dropped={dropped} kept={len(out)}")

if __name__ == '__main__':
    files = sys.argv[1:] or ['fortunes-sfw.txt', 'fortunes-spicy.txt']
    for f in files:
        process(f)
