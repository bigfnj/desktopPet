# Fortune classification taxonomy (locked 2026-07-29)

**Four independent axes** per fortune. Two are new (this classification pass), two already exist.

| axis | values | assigned by | purpose |
|------|--------|-------------|---------|
| **topic** (new) | 11, below | this pass | *subject* — the light routing nudge (screen→topic prototype) |
| **genre** (new) | 12, below | this pass | *format/delivery* — the corpus's real variety; powers flavor/filter |
| `level` (exists) | general / edgy / nsfw | `classify-corpus.py` | content **severity** → spicy-tier filter |
| `prof` (exists) | 0 / 1 | `classify-corpus.py` | profanity flag → "remove profanity" filter |

Grounded in a scan of all 61k lines: the corpus varies far more by **genre** than **topic**
(TV quotes 22%, showerthoughts 16%, wisdom 17%, jokes 9% — and every topic keyword signal is ≤4%),
so genre is the rich axis and `life` is a legitimately large topic catch-all. Severity (`level`/`prof`)
stays **orthogonal**: Carlin can be `genre=dark, level=general`; a wholesome f-bomb is `uplifting, edgy`.

Schema after the pass: `source ⇥ topic ⇥ genre ⇥ level ⇥ prof ⇥ text`.

---

## TOPIC — subject (11).  Exemplars double as the routing prototypes.

| topic | subject | routing prototypes |
|-------|---------|--------------------|
| **tech** | computing, programming, the internet, gadgets | "Debugging is twice as hard as writing the code." · "The cloud is just someone else's computer." |
| **science** | physics, space, biology, math, how reality works | "Every atom in you was forged in a dying star." · "Light from that star left before you were born." |
| **work-money** | jobs, business, productivity, finance, wealth | "A meeting is where hours go to die." · "A budget is telling your money where to go." |
| **love** | dating, marriage, breakups, intimacy | "We broke up; it just wasn't working out." · "Love is one soul inhabiting two bodies." |
| **family** | parents, kids, home, growing up | "Kids remember the time, not the toys." · "My father's gift was believing in me." |
| **faith** | religion, spirituality, the sacred, meaning | "The soul never thinks without a picture." · "Faith is taking the first step without seeing the staircase." |
| **society** | politics, culture, law, history, current events | "The first duty of a citizen is to question authority." · "History doesn't repeat, but it rhymes." |
| **food** | cooking, eating, drink | "There is no love more sincere than the love of food." · "You bring the soul to the recipe." |
| **nature** | animals, weather, the outdoors, the environment | "A dog loves you more than it loves itself." · "The mountains are calling and I must go." |
| **arts** | literature, writing, film, visual art, music, creativity | "Write drunk, edit sober." · "Without music, life would be a mistake." |
| **life** | everyday life, small observations, the human comedy (catch-all) | "The best part of the day is the first sip of coffee." · "Websites should list their password rules on the login page." |

## GENRE — format / delivery (12)

| genre | delivery | example |
|-------|----------|---------|
| **tv-quote** | character / screen dialogue | "George: A woman that hates me this much comes along once in a lifetime." |
| **observation** | shower-thought noticing of the mundane | "Dogs lick us because we have bones inside." |
| **joke** | setup → punchline gag (incl. dad jokes) | "Why don't scientists trust atoms? They make up everything." |
| **pun** | wordplay / groaner | "I used to be a banker but I lost interest." |
| **quip** | witty one-liner / wry aside | "I'm not arguing, I'm just explaining why I'm right." |
| **aphorism** | proverb / maxim / saying | "Time and tide wait for no man." |
| **wisdom** | earnest philosophical insight | "The unexamined life is not worth living." |
| **fact** | trivia / factoid, no gag or moral | "A honey bee can fly at 15 mph." |
| **insult** | roast / yo-mama | "Yo momma's so stubborn she argues with the weather forecast." |
| **verse** | limerick / poem / deliberate rhyme | "There once was a man from Nantucket…" |
| **dark** | gallows / cynical / morbid humor | "I want to die in my sleep like grandpa — not screaming, like his passengers." |
| **uplifting** | wholesome or motivational | "You are braver than you believe and stronger than you seem." |

---

## Labeling rules

- Exactly **one topic + one genre** per fortune. Pick the *dominant* read.
- **`life`** is the topic catch-all (expect it to be large); **there is no genre catch-all** — every line
  has a delivery style, so choose the closest of the 12.
- **topic ≠ source.** A `quotable` line about code is `tech`; a Simpsons line about a divorce is
  `love`+`tv-quote`.
- **genre vs `level` are different questions:** *how it reads* (dark/insult) vs *how blue it is*
  (edgy/nsfw). Don't infer one from the other.
- Prototypes are editable — retuning a topic's prototype sentences retunes routing with **no relabeling**.

## Optional future merges (not applied)
`work-money`→split · `arts` (could split music) · `pun`→`joke`. Left as-is; revisit if a bucket is thin.
