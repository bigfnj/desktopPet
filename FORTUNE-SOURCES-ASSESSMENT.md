# Fortune Sources — Harvest & Assessment

Working doc for the Fortune Sheep corpus. Every source below was downloaded, normalized to
bubble-sized entries (8–280 chars, whitespace-collapsed), then run through the **real shipping
pipeline** (`strip-authors.py` + `classify-corpus.py`). Columns:

- **n** — deduped entries that survived the full pass
- **edgy / nsfw** — how many our classifier flagged (the rest are `general`)
- **rec** — `INCLUDE` (ship in default embedded core) · `PACKAGE` (opt-in GitHub download) · `IGNORE`

Raw combined harvest: `scratchpad/harvest/fortunes-harvest.txt` (+ `bofh.txt`).
Total harvested (pre-curation): **~41,800 entries** across ~120 source files.

Harvest caveats: Mitch Hedberg gist returned the scrape *script* not jokes (junk); type.fit's
endpoint is dead (5 rows); fortune-tv ships compiled `.dat` binaries that were ingested as noise
(excluded here); the public-domain books over-count because segmentation swept in translator
prefaces (flagged per row).

The **Tier A/B/C** sections below are *new candidates to add*. The section immediately below is
what we **already ship today** (the 69 JKirchartz-derived sources), with keep/trim/prune calls.

---

## What we already ship — 69 sources, 26,141 entries (keep / trim / prune)

`n` = current entries · `edgy`/`nsfw` = classifier flags · rec = `KEEP` / `TRIM` (too big or noisy) /
`PRUNE` (not fortune-like) / `REVIEW` (unclear origin/quality).

| Source | n | edgy | nsfw | rec | Note |
|---|--:|--:|--:|---|---|
| showerthoughts | 9,909 | 494 | 411 | **TRIM** | 38% of the whole corpus; Reddit UGC, noisy — quality-filter/dedupe down to the best |
| authors | 2,199 | 7 | 4 | KEEP | Goodreads-style author quotes (bylines now stripped) |
| classic_philosophy | 1,071 | 0 | 0 | KEEP | |
| yo-mama | 979 | 971 | 8 | KEEP | spicy (edgy tier) |
| modern_philosophy | 976 | 2 | 0 | KEEP | |
| artists | 928 | 2 | 1 | KEEP | |
| realfacts | 861 | 0 | 0 | KEEP | trivia "facts" |
| conalnet | 710 | 3 | 0 | **REVIEW** | unclear origin — verify quality/provenance |
| PA-historical-markers | 697 | 1 | 0 | **PRUNE** | historical-marker text ("Erection begun 1772…"), not fortune-like |
| chuckfacts | 479 | 30 | 10 | KEEP | Chuck Norris facts |
| godin | 401 | 1 | 0 | KEEP | Seth Godin |
| Rousseau | 396 | 0 | 0 | KEEP | |
| handey | 389 | 3 | 5 | KEEP | Jack Handey Deep Thoughts |
| tao | 385 | 0 | 0 | KEEP | |
| hackers | 377 | 2 | 0 | KEEP | |
| lwall-quotes | 370 | 1 | 0 | KEEP | Larry Wall |
| SimpsonsChalkboard | 365 | 1 | 1 | KEEP | |
| activists | 357 | 3 | 0 | KEEP | |
| entertainers | 355 | 2 | 0 | KEEP | |
| Paine | 330 | 2 | 3 | KEEP | Thomas Paine |
| FerengiRulesOfAcquisition | 294 | 0 | 0 | KEEP | |
| redgreen | 256 | 1 | 0 | KEEP | The Red Green Show |
| anathem-glossary | 160 | 0 | 0 | KEEP | |
| rhetorical-devices | 159 | 0 | 0 | KEEP | |
| EnglishAsSheIsSpoke | 152 | 0 | 0 | KEEP | |
| mencken | 151 | 0 | 0 | KEEP | |
| MrRogers | 142 | 0 | 0 | KEEP | |
| Gurdjieff | 137 | 0 | 0 | KEEP | |
| ObliqueStrategies | 136 | 0 | 0 | KEEP | |
| critics | 123 | 0 | 2 | KEEP | |
| epigrams_in_programming | 120 | 0 | 0 | KEEP | |
| pirate | 120 | 0 | 0 | KEEP | |
| subgenius | 118 | 9 | 3 | KEEP | Church of the SubGenius |
| Andromeda | 111 | 1 | 0 | KEEP | sci-fi quotes |
| HeraclitusFragments | 109 | 0 | 0 | KEEP | |
| montaigne | 106 | 0 | 0 | KEEP | |
| immortal_consciousness | 82 | 0 | 0 | KEEP | |
| groucho | 80 | 0 | 0 | KEEP | Groucho Marx |
| SimoneWeil | 79 | 0 | 0 | KEEP | |
| wblake | 78 | 0 | 0 | KEEP | William Blake |
| actualcookies | 71 | 0 | 0 | KEEP | fortune-cookie sayings |
| jung | 71 | 0 | 0 | KEEP | |
| RAW | 70 | 0 | 1 | KEEP | Robert Anton Wilson |
| SeventyMaximsOfMaximallyEffectiveMercenaries | 70 | 0 | 0 | KEEP | |
| Jenny_Holzer | 60 | 0 | 0 | KEEP | |
| ComputerDictionary | 57 | 0 | 0 | KEEP | |
| carlin | 53 | 53 | 0 | KEEP | George Carlin (spicy) |
| invisiblestates | 50 | 0 | 0 | KEEP | |
| ogden_nash | 49 | 0 | 0 | KEEP | |
| **BibleAbridged** | **48** | 2 | 0 | **KEEP** | "The Holy Bible: Abridged Beyond the Point of Usefulness" — the one you asked about |
| enkiv2s-glossary-of-tech-industry-terms | 45 | 0 | 1 | KEEP | |
| Bakunin | 38 | 0 | 0 | KEEP | |
| Kerouac-Modern-Prose | 30 | 0 | 0 | KEEP | |
| Twenty_Lessons_On_Tyranny | 19 | 0 | 0 | KEEP | |
| AClaude | 18 | 0 | 0 | **REVIEW** | odd "Claude" source — verify |
| higgins_metadramas | 17 | 17 | 0 | **REVIEW** | obscure, all flagged edgy |
| haraway | 16 | 0 | 0 | KEEP | |
| predictions | 16 | 0 | 0 | KEEP | |
| stevenson | 15 | 1 | 0 | KEEP | |
| hacker-questions | 13 | 0 | 0 | KEEP | |
| brecht_dances-events-puzzles | 12 | 0 | 0 | KEEP | |
| bruno-latour | 11 | 0 | 0 | KEEP | |
| rfc1925 | 11 | 0 | 0 | KEEP | |
| ObscureSorrows | 9 | 2 | 0 | KEEP | Dictionary of Obscure Sorrows |
| friedman_12-structures | 7 | 0 | 0 | KEEP | |
| korzybski | 7 | 0 | 0 | KEEP | |
| Schlesinger | 5 | 0 | 0 | KEEP | |
| racter | 4 | 0 | 0 | KEEP | AI-generated novelty |
| existentialriddles | 2 | 0 | 0 | KEEP | |

**Existing takeaways:** the corpus is dominated by `showerthoughts` (38%) — trimming it is the single
biggest quality win. `PA-historical-markers` isn't fortune-like (prune). `conalnet` / `AClaude` /
`higgins_metadramas` need a provenance/quality look. Everything else is worth keeping.

---

## Tier A — INCLUDE (clean, legally defensible default core — NEW additions)

### Quotes & jokes

| Source | n | edgy | nsfw | License | Notes |
|---|--:|--:|--:|---|---|
| **quotable** | 2,114 | 1 | 0 | **MIT** | Pristine curated famous quotes. Zero-risk gold standard. |
| **cleanjokes** | 1,611 | 5 | 2 | Reddit UGC (low risk) | Clean dad-joke / pun set; beloved. |

### fortune-mod — canonical clean datfiles (fair-use, the 40-year-old Unix set)

| Source | n | edgy | nsfw | Notes |
|---|--:|--:|--:|---|
| people | 1,181 | 0 | 0 | Quips about people/life |
| definitions | 1,098 | 6 | 0 | Devil's-dictionary style |
| cookie | 893 | 13 | 1 | Classic fortune-cookie file |
| computers | 819 | 4 | 0 | Hacker/computing humor |
| miscellaneous | 629 | 1 | 0 | |
| politics | 625 | 0 | 0 | |
| work | 546 | 1 | 0 | |
| zippy | 541 | 0 | 3 | Zippy the Pinhead (Griffith, mild ©) |
| men-women | 512 | 0 | 0 | |
| science | 508 | 2 | 0 | |
| platitudes | 488 | 1 | 0 | |
| songs-poems | 436 | 2 | 0 | |
| fortunes | 431 | 0 | 0 | The core `fortunes` file |
| art | 400 | 0 | 2 | |
| wisdom | 385 | 1 | 2 | |
| linux | 359 | 5 | 1 | |
| perl | 270 | 1 | 0 | |
| literature | 212 | 0 | 0 | |
| humorists | 195 | 0 | 0 | |
| education | 181 | 0 | 0 | |
| food | 164 | 0 | 0 | |
| law | 145 | 0 | 1 | |
| love | 138 | 0 | 0 | |
| kids | 124 | 0 | 0 | |
| riddles | 116 | 0 | 0 | |
| shlomif-fav | 113 | 1 | 1 | Maintainer favorites |
| sports | 101 | 0 | 0 | |
| debian | 75 | 1 | 1 | IRC-quote style |
| paradoxum | 68 | 0 | 0 | |
| medicine | 51 | 0 | 1 | |
| pets | 47 | 0 | 0 | |
| news | 44 | 0 | 0 | |
| goedel | 43 | 0 | 0 | |
| magic | 19 | 0 | 0 | |
| tao | 6 | 0 | 0 | (we already ship a Tao source) |

**fortune-mod clean subtotal ≈ 11,965** (excludes pratchett/startrek → Tier B, and ethnic/drugs/x-files → Tier C).

### Public-domain wisdom (INCLUDE, but needs a verse-segmentation pass)

| Source | n raw | usable est. | License | Notes |
|---|--:|--:|---|---|
| meditations (Marcus Aurelius) | 2,166 | ~800–1,000 | **Public domain** | Count inflated by translator's bio/preface; trim to Books I–XII |
| artofwar (Sun Tzu, Giles) | 1,796 | ~400 | **Public domain** | Front-matter + commentary mixed in |
| analects (Confucius, Legge) | 1,519 | ~500 | **Public domain** | Front-matter + verse numbers swept in |
| dhammapada | 422 | ~300 | **Public domain** | Cleanest of the four |

**Tier A total ≈ 18,000–19,000** legally-defensible entries after PD curation.

---

## Tier B — PACKAGE (opt-in downloads, not in default)

### B1 · BOFH (tech) — NEW

| Source | n | edgy | nsfw | License | Notes |
|---|--:|--:|--:|---|---|
| **bofh** | 489 | 2 | 2 | © Travaglia / near-folklore | Canonical BOFH Excuse Server list, formatted `BOFH excuse #N: …`. Merged from `shriramters/maubot-bofh` + `fundor333/bofh`. Slots next to the tech sources. |

### B2 · Copyrighted authors (beloved, but ©)

| Source | n | License | Notes |
|---|--:|---|---|
| pratchett | 558 | © Terry Pratchett | Discworld quotes; superb, but copyrighted |
| startrek | 220 | © Paramount | |

### B3 · Adult / NSFW (`fortune -o`, minus the hateful files)

| Source | n | edgy | nsfw | Notes |
|---|--:|--:|--:|---|
| off·atheism | 1,358 | 24 | 14 | More irreverent than obscene |
| off·limerick | 965 | 220 | 157 | Dirty limericks; raunchiest single file |
| off·sex | 592 | 41 | 43 | |
| off·definitions | 304 | 30 | 24 | Crude definitions |
| off·politics | 252 | 12 | 3 | |
| off·riddles | 248 | 11 | 13 | |
| off·black-humor | 172 | 9 | 4 | |
| off·religion | 155 | 4 | 0 | |
| off·vulgarity | 140 | 91 | 3 | |
| off·songs-poems | 114 | 12 | 2 | |
| off·privates | 76 | 12 | 7 | |
| off·drugs | 57 | 1 | 0 | |
| off·miscellaneous | 52 | 0 | 2 | |
| off·astrology | 32 | 0 | 0 | |
| off·debian | 19 | 7 | 3 | |
| off·knghtbrd | 13 | 13 | 0 | |
| off·linux | 7 | 4 | 0 | |

**NSFW pack subtotal ≈ 4,560.**

### B4 · Fandom — fortune-tv (© copyright, personal-use; dialogue-format, avg ~140 chars)

Cleaner shows first; **profane** = classifier edgy+nsfw hits.

| Show | n | profane | | Show | n | profane |
|---|--:|--:|---|---|--:|--:|
| simpsons | 1,616 | 43 | | southpark | 864 | 207 |
| mst3k | 1,576 | 22 | | trailerparkboys (tpb) | 326 | 155 |
| futurama | 793 | 39 | | sopranos | 234 | 133 |
| x-files | 343 | 12 | | peepshow | 544 | 125 |
| office-us | 249 | 13 | | thewire | 179 | 91 |
| 30rock | 230 | 15 | | 3rdrock | 167 | 102 |
| qi | 229 | 18 | | beavisbutthead | 751 | 96 |
| madmen | 203 | 11 | | drawntogether | 381 | 93 |
| firefly | 198 | 9 | | boondocks | 150 | 70 |
| scrubs | 198 | 18 | | archer | 342 | 64 |
| seinfeld | 186 | 4 | | alwayssunny | 220 | 51 |
| batman (+TAS) | 260 | 17 | | mrshow | 208 | 50 |
| parksrec | 165 | 16 | | venturebros | 382 | 45 |
| arrested | 156 | 7 | | metalocalypse | 164 | 31 |
| moralorel | 155 | 2 | | curb | 119 | 35 |
| malcolm | 393 | 14 | | friskydingo | 152 | 35 |
| koth (King of the Hill) | 384 | 21 | | sealab2021 | 255 | 30 |
| homemovies | 140 | 5 | | genkill | 43 | 23 |
| twilightzone | 54 | 0 | | squidbillies | 88 | 10 |
| montypython | 49 | 3 | | youngones | 62 | 7 |
| a-team | 27 | 0 | | robotchicken | 38 | 7 |
| lookaroundyou | 46 | 1 | | harveybirdman | 115 | 5 |
| bobsburgers | 45 | 1 | | snl | 102 | 11 |
| dilbert | 44 | 1 | | newsradio | 81 | 2 |
| rockos | 94 | 1 | | lucydevil | 41 | 9 |
| workaholics | 6 | 1 | | rickmorty | 3 | 1 |

**Fandom subtotal ≈ 13,850** across 51 shows. Suggest grouping into a **clean** pack (Simpsons, MST3K, Futurama, Star Trek/X-Files, Firefly, Seinfeld, Parks & Rec, Twilight Zone, Monty Python, A-Team…) and a **mature** pack (South Park, Sopranos, The Wire, Trailer Park Boys, Always Sunny, Archer…).

---

## Tier C — LEGALLY DUBIOUS / IGNORE

| Source | n | Why ignore |
|---|--:|---|
| off·racism | 4 | Hate content — drop (FreeBSD removed these in 2017) |
| off·misogyny | 49 | e.g. "A little bit of rape is good for a man's soul" — vile. Drop |
| off·hphobia | 13 | Homophobic. Drop |
| off·misandry / off·ethnic | 86 | Same category. Drop |
| fm·ethnic | 127 | In the "clean" set but dated ethnic jokes — review, don't auto-ship |
| fm·drugs | 159 | Drug recipes/jokes — review |
| fm·the-x-files-taglines | 24 | Mojibake (broken Navajo encoding) |
| hedberg | 8 | Harvest got the scrape *script*; and it's copyrighted |
| typefit | 5 | Endpoint effectively dead; overlaps Quotable |
| Quotes-500K / joke megadumps / dadjokes / copypasta | — | Not harvested: scraped-from-Goodreads/Reddit, legally murky, heavy filtering needed |

---

## Recommendation

**Default embedded core (Tier A only):** Quotable (MIT) + fortune-mod clean canonical files +
cleanjokes + the four public-domain wisdom texts (after segmentation). A legally-defensible
**~18–19k** corpus — a big upgrade over the current showerthoughts-heavy set.

**Opt-in GitHub packs (Tier B):**
- **BOFH** (489, tech) ← your request
- **Fandom / clean** and **Fandom / mature** (fortune-tv)
- **NSFW** (`fortune -o` minus hate)
- **Pratchett & Trek**

**Drop entirely (Tier C):** the hate files, dated ethnic/drugs, and the broken/dubious harvests.

### Open questions before cutting packages
1. OK to drop Tier C (hate/ethnic/drugs) outright?
2. Public-domain books: invest in a proper verse-segmentation pass, or ship v1 core as
   Quotable + fortune-mod + cleanjokes + BOFH and add PD wisdom later?
3. BOFH: keep as an opt-in `tech` pack, or promote into the default core?
