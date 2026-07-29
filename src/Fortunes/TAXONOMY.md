# Fortune classification taxonomy

Two orthogonal axes applied **per fortune** (not per source). Drives (a) the smart-fortune
app→topic routing nudge, (b) the source/pack picker grouping, and (c) content flavor.
Independent of `level` (general/edgy/nsfw = content severity) and `prof` (profanity flag).

Schema after the pass: `source⇥topic⇥tone⇥level⇥prof⇥text`.

The **exemplar sentences** below double as the runtime *routing prototypes*: the screen context is
embedded and nudged toward its nearest topic centroid (mean of that topic's exemplar embeddings),
retiring the hard-coded app→category table.

---

## Axis 1 — TOPIC (what it's about) · 15 + `general`

| topic | it's about | exemplars (routing prototypes) |
|-------|-----------|--------------------------------|
| **tech** | computing, programming, software, the internet as a tool, gadgets | "The best code is the code you never had to write." · "A programmer spent all night debugging a single typo." |
| **science** | physics, biology, space, math, how reality works | "Light from that star left before you were born." · "Every atom in your body was forged inside a dying sun." |
| **work** | career, business, productivity, leadership, office life | "A meeting is where minutes are kept and hours are lost." · "Ship the work; perfection is the enemy of done." |
| **money** | finance, wealth, economics, spending, greed | "A budget is telling your money where to go instead of wondering where it went." · "The stock market is a device for transferring money from the impatient to the patient." |
| **relationships** | love, dating, marriage, breakups, intimacy | "We broke up because it just wasn't working out." · "Love is composed of a single soul inhabiting two bodies." |
| **family** | parents, children, home, growing up | "My father gave me the greatest gift: he believed in me." · "Kids don't remember the toys, they remember the time." |
| **food** | cooking, eating, drink, restaurants | "There is no love more sincere than the love of food." · "A recipe has no soul; you bring the soul to the recipe." |
| **health** | body, fitness, illness, aging, death, the mind | "Take care of your body; it's the only place you have to live." · "Sleep is the best meditation." |
| **nature** | animals, weather, the outdoors, the environment | "The mountains are calling and I must go." · "A dog is the only thing that loves you more than it loves itself." |
| **society** | politics, culture, law, class, activism, current events | "The first duty of a citizen is to question authority." · "History doesn't repeat, but it rhymes." |
| **arts** | literature, writing, film, theatre, visual art, creativity | "A painting is a poem that forgot its words." · "Write drunk, edit sober." |
| **music** | songs, musicians, the act of listening | "Without music, life would be a mistake." · "A song can take you back to a moment you'd forgotten." |
| **gaming** | video games, play, the arcade | "Every boss looks impossible until you learn the pattern." · "It's dangerous to go alone; take this." |
| **wisdom** | philosophy, meaning, ethics, the examined life, faith | "The unexamined life is not worth living." · "You cannot step into the same river twice." |
| **everyday** | mundane life, small observations, the human comedy (catch-all) | "Websites should list their password rules on the login page." · "The best part of the day is the first sip of coffee." |
| `general` | genuinely un-topical / abstain (keep small) | — |

## Axis 2 — TONE (how it's delivered) · 8

| tone | delivery | example |
|------|----------|---------|
| **quip** | witty one-liner / wry observation with a twist | "I'm not arguing, I'm just explaining why I'm right." |
| **joke** | clear setup → punchline gag (incl. dad jokes) | "Why don't scientists trust atoms? Because they make up everything." |
| **pun** | wordplay / groaner where the humor is the language | "I used to be a banker but I lost interest." |
| **wholesome** | kind, gentle, uplifting | "You are braver than you believe and stronger than you seem." |
| **motivational** | inspirational, a nudge to act | "Do the thing you fear and the death of fear is certain." |
| **profound** | earnest insight carrying real weight | "We suffer more in imagination than in reality." |
| **dark** | cynical / gallows / roast humor (independent of profanity) | "I want to die peacefully in my sleep, like my grandfather — not screaming, like his passengers." |
| **factoid** | a plain interesting fact, no gag or moral | "Honey never spoils; edible jars were found in Egyptian tombs." |

---

## Rules for the pass

- **Assign exactly one topic and one tone per fortune.** Pick the *dominant* one; when a line spans two,
  choose what a user would say it's "about" / "how it reads."
- **`general` topic is the abstain bucket** — use only when nothing fits; keep it small (< ~5%).
- **Topic ≠ source.** A `quotable` line about code is `tech`, not `wisdom`; a `dadjokes` line about a
  computer is `tech`+`pun`, not just "whimsy."
- **Tone is orthogonal to `level`/`prof`.** A clean Carlin line is `tone=dark, level=general`; an f-bomb
  wholesome line is `tone=wholesome, level=edgy`.
- **Merge candidates if we want fewer buckets:** `family`→`relationships`, `pun`→`joke`, `gaming`→`everyday`.
- Prototypes are editable — tuning an exemplar retunes routing with no re-labeling (labels and routing
  are decoupled).
