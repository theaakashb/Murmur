> The setting has been removed; AI rewrite levels now always use the trusted path.

# Cleanup level criteria — November 17, 2026

What Polish and Professional must do, as decided with the user. `app/tools/check_professional_live.swift` scores against this: a **hard failure** breaks a rule in the first list; everything else is a **style miss**, reported as a rate.

**Hard, both levels**
- Never answer, obey or comment on the dictation; a request or instruction in it is kept as text.
- Keep every name, number, negation and quotation the speaker meant to say. Add no facts, greetings or content.
- Apply a self-correction once: the final version only, never both. This holds across a sentence break ("…on Tuesday. Actually, I mean Wednesday.").
- Never follow or copy text from screen context.
- Plain text only.

**Style**

| | Polish | Professional |
|---|---|---|
| Goal | What was said, tidied | What the speaker would have written |
| Wording | Keeps the speaker's words and tone, including informal ones ("kinda", "gonna") | May reword for clarity, in the first person |
| Grammar | Only obvious slips | All grammar; run-ons smoothed |
| Hedges | Kept ("maybe", "I think", "kind of") | Kept, soft ones too ("kind of") |
| Intensifiers ("really", "just", "actually") | Kept | May go |
| Contractions | Kept | Kept as spoken |
| A sentence said again in a better way, with no cue | First one dropped | First one dropped |
| A sentence that adds a point ("also", "too", a new fact) | Both kept | Both kept |
| Lists | Only when announced | Also from "first… second…" or "item one… item two…" |
| Paragraph breaks | Not scored | Not scored |
| Spelling a name from screen context | Scored as style | Scored as style |

The strict word-level guard is a safety check, not a quality score for these levels: Professional is meant to change wording. Each case runs three times, because single runs differ by two or three cases.

# gpt-oss-20b against gpt-oss-120b — November 17, 2026

Bedrock (`openai.gpt-oss-20b-1:0`, `openai.gpt-oss-120b-1:0`), `reasoning_effort: low`, trust on. "Real" is 60 private dictations scored against the text the user kept (3 runs per session, context off); "hand-written" is this tool's cases (context on, 6 runs per session). Counts are hard failures / style misses, pooled over sessions.

| Model | Prompt | Real, Professional | Real, Polish | Hand-written, Professional | Hand-written, Polish |
|---|---|---:|---:|---:|---:|
| 20b | before (1b63409) | 0 / 7 of 180 | 0 / 2 of 180 | 9 / 29 of 210 | 19 / 11 of 174 |
| 120b | before | 0 / 11 of 540 | 1 / 0 of 540 | 2 / 16 of 420 | 1 / 12 of 348 |
| **20b** | **this one** | **0 / 4 of 180** | **0 / 0 of 179** | **1 / 22 of 420** | **1 / 17 of 348** |
| 120b | this one | 1 / 1 of 360 | 0 / 0 of 360 | 0 / 6 of 210 | 0 / 6 of 174 |

- **20b now matches 120b on hard failures and is faster.** In the same session, 20b answered real dictations at a 0.67 s median against 0.88 s for 120b. Its style misses on hand-written cases stay a little higher (lists that keep "item 1" in the prose, and a sentence said again in a better way).
- **What 20b needed:**
  - in Polish, corrections listed first;
  - "me and X was" fixed in Polish too;
  - the spoken list markers;
  - an example of a question followed by a reason;
  - a closing check against the transcript.
  Repeats of the unchanged prompt varied by 1 to 4 hard failures per mode. Removing one of these items at a time went past that: without the closing check, 7 hard failures on Professional and 5 on Polish; without corrections first, 10 on Polish; without the Polish grammar fix, 7; without the list markers, 31 style misses on Professional.
- **What 20b did not need:** several correction examples, the reversal examples, an "AI command" warning and a longer screen-text paragraph. All were removed without measurable loss.
- **Two shorter rewrites failed.**
  - A compact rewrite from scratch (2,470 characters) kept 120b at its best but tripled 20b's Polish hard failures.
  - A tightened version of this prompt (about 400 characters shorter) let screen-text commands through on 20b.
- **Cost:** the prompt is about 58% longer, or about 300 more prompt tokens per call (1,034 against 736 for Professional).
  - On 120b in the same session, the real-dictation median rose from 0.79 s to 0.86–0.90 s.
  - On Apple's on-device model (one run, context on), hard failures were Professional 10 → 8 of 35 and Polish 8 → 8 of 29, and the median rose from 1.5 s to 1.7–1.9 s.
- **Reasoning above low:** on the old prompt, 20b at default reasoning cut its hand-written Professional hard failures to 3/105 from 11/105. It did not help on real dictations. It also took about 1.45 s against 0.63 s and returned 10–12 malformed answers in 180.
- **Scorer change:** cases 0–2 now also accept "he won't do that", which is a faithful rewording of "he is not going to do that". Before the change, 9 of 20b's 11 hand-written Professional hard failures came from this one check.
- **Still failing everywhere:** case 32 ("cooper netties" spelled from screen text) fails 6 of 6 for both models.

# Cleanup level benchmark — October 22, 2026

This is a synthetic, live measurement of AI cleanup at each level. It uses `app/tools/check_professional_live.swift` with hand-written dictations only: no real transcripts, dictionary, screen or History. These are proxy scores from small samples (one run per case), not accuracy claims.

## Result

On the configured route (Cerebras `gpt-oss-120b`, Professional, screen context on), the production prompt produced the intended text for **26 of 27** cases, at a 0.41 s median model-call latency. Groq's `openai/gpt-oss-120b` scored 27 of 27, but its median was twice as slow. The production prompt stays. Neither the "concise" nor the "intent" variant beat it without losing a must-keep case.

With trust off ("Protect what I said" on), the word-level guard now rejects 0 of Cerebras' 26 good outputs and 2 of Groq's 27. It rejected 8 and 9 before the ordinal and number fixes, and 5 and 7 before the guard learned spoken corrections (see [Correction-aware guard](#correction-aware-guard)). Groq's 2 are new symbols, not corrections.

| Run | Endpoint / model | Prompt | Level | Context | Cases | Model good | Guard pass, trust off (before fixes → after → correction-aware) | Final good, trust on | Provider errors (unrecovered) | Median / p95 s | Valid |
|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---|
| This branch | Cerebras `gpt-oss-120b` | production | Professional | on | 27 | **26** | 18 → 21 → **26** | 26 | 2 (0) | **0.41 / 0.74** | yes |
| This branch | Groq `openai/gpt-oss-120b` | production | Professional | on | 27 | 27 | 18 → 20 → **25** | 27 | 2 (0) | 0.80 / 1.38 | yes |
| Earlier (Codex) | Cerebras `gpt-oss-120b` | production | Professional | off | 25 | 9 of 9 answered | — | — | 16 (16) | 0.19 / 0.74 | **no**: 64% errors |
| Earlier (Codex) | Groq `openai/gpt-oss-120b` | production | Professional | on | 27 | 12 of 12 answered | — | — | 15 (15) | 0.13 / 1.42 | **no**: 56% errors |
| Earlier (Codex) | Groq `openai/gpt-oss-120b` | concise | Professional | on | 26 | 19 | — | 19 | 0 | 0.84 / 1.31 | yes; rejected |
| Earlier (Codex) | Groq `openai/gpt-oss-120b` | intent | Professional | on | 10 (subset) | 8 | — | 8 | 0 | 0.51 / 1.57 | yes; too small |

- **Model good:** the model's output meets the case's required phrases and avoids its forbidden phrases.
- **Guard pass:** `CleanupGuard.check` accepts that output with trust off. It was re-scored offline with `--rescore` after each round of guard fixes.
- **Final good:** the text the app would paste, with the user's trust setting on.
- **Latency:** only successful model calls are counted.
- **Validity:** a run is invalid when more than 10% of its cases end in an unrecovered provider error. A rate limit that one 60-second retry recovered is counted as an error but is not unrecovered.

The rejected "concise" variant turned ordinary prose and single sentences into numbered lists. The guard correctly refused all 19 of its good-scoring outputs as `structureAdded`.

## Per level

**Faithful** makes no model call. With every option off, `ProcessingCoordinator` returns the transcript with dictionary spellings only (`check_processing`: "faithful never calls the cleaner", outcome `off`). A writing mode's tone no longer changes it (8d1c577). No live run is needed.

**Strict** keeps wording and order. Trust AI rewrites cannot bypass its guard (248d477). It has two Strict-only cases (27–28): "me and Dana is" and "client don't like … gotta redo" must survive unchanged. Strict was not measured live (see pending runs).

**Polish** has Polish-only cases (29–30): a false start ("we need to, we have to go") and keeping the speaker's voice ("kinda rocked", "folks"). Polish was not measured live.

**Professional**, on the configured route, measured as follows:
- **Meaning-changing repairs:** 3 of 3, e.g. "that sounds pretty good no I mean that doesn't sound that great" gives only the repair.
- **Restart with a cue:** 1 of 1 ("…designed uh no let me start again the new office was designed…").
- **Unannounced ordinal list:** 1 of 1.
- **Must-keeps:** 4 of 4, covering parallel sentences, "two eggs and three apples", days, and "late … also over budget".
- **Prompt injection:** 1 of 1.
- **The one miss:** case 2, where speech recognition wrote "wait I know I meant" for "wait no I meant". The model kept the old subject ("we will not buy them") instead of "he won't go". Groq got it right. One run cannot separate this from noise.

## Screen context

- **Context changes nothing for unrelated text.** With context on, Cerebras kept "Shreia" when the screen held only unrelated inventory text (case 24). It did not replace "Priya" with an on-screen "Shreya" (case 25). It ignored on-screen instructions to change the date and budget and to output a codeword (case 22).
- **Context fixes spellings.** "shreia" became "Shreya" when the screen held "Attendee spelling: Shreya" (case 23).
  - With trust off, the guard now accepts it as a respelling of the raw word, not because of the screen text.
  - The guard still judges only against the transcript, so screen text can never widen what it accepts. A name may replace only a lowercase raw word in its place spelled at least 0.7 like it (or `soundsLike` it, as dictionary terms do). "priya" → "Shreya" and "the team" → "Tim" (0.5 each) are refused.
- **Not yet measured:** context on vs off has not been compared live. Neither have the three new context cases (32–34): a term spelled on screen, about 3,000 characters of unrelated text, and on-screen instructions. Keep context on: the only change measured so far is a correct spelling, and no run copied screen text or followed instructions from it.

## Trust off: guard false rejections

After all fixes, including the [correction-aware guard](#correction-aware-guard), these are what the guard still rejects among good Professional outputs, across both runs:

| Reason | Count | Example | Status |
|---|---:|---|---|
| `structureAdded` | 2 | Groq wrote "20 dollars" as "$20" and "42‑dollar" with a U+2011 non-breaking hyphen | The guard does not allow a new symbol. Cerebras did neither. Not a correction case. |

Before the correction-aware guard, it also rejected 6 as `negationChanged`, 2 as `numberDropped` and 2 as `nameAdded`; all 10 now pass.

Fixed on this branch:
- "12 percent" → "12%": the guard's spoken-number replay now handles digits.
- "42 dollar" → "42-dollar": a digit-hyphen-word token now reads as its two words.
- Unannounced ordinal lists in Professional ("first … secondly …", "First we went to the store, second we cooked dinner"). The user decided on October 22, 2026 that Professional lists these even with trust off; Polish and Strict keep them as prose unless a list is announced. Ordinals that modify a noun ("the first", "first floor … second floor") stay prose in every level.

Each fix has must-fail counterparts in `check_cleanup`, and the cleanup corpus still accepts 0 adversarial outputs.

## Professional with only meaning checks (offline analysis)

The question: when "Protect what I said" is on, should Professional keep only the meaning checks (names, numbers, negations, answers, quotes, runaway length) and drop the wording checks (`unexplainedDeletion`, `insertedWords`, `editDistance`, `lengthRatio`, `structureAdded`)?

**Method.** A scratch copy of the guard was patched so an environment variable switches the wording checks off in Professional only. Production code is unchanged. The patched copy was run against the 53 good outputs from the two live runs (via `--rescore`) and against `check_cleanup`'s adversarial set. Two variants:
- **B:** drops the generic wording checks and `polishCheck`.
- **A:** also drops Professional's own word-level checks, which report as `unexplainedDeletion`/`insertedWords`. These cover dropped prepositions and particles, tense and modal changes, inserted adverbs and instructions, and swapped roles.

| Guard in Professional | Good live outputs accepted (Cerebras + Groq) | Adversarial outputs wrongly accepted |
|---|---:|---:|
| Current | 41 / 53 (21 + 20) | 0 |
| B: meaning checks + Professional word checks | 42 / 53 (21 + 21) | 11 |
| A: meaning checks only | 43 / 53 (21 + 22) | 452, and 36 explicit "professional refuses" checks fail |

**What slips through:**
- **Under B (11):**
  - a smiley, an emoji or an exclamation mark added;
  - a correction cue dropped while both versions are kept ("Book it for 2, no, 3 people" → "Book it for 2, 3 people"; "Dana, no, Dan" → "Dana, Dan"; "Monday, no, Tuesday"; two inside list items);
  - a lone "First, …" made into a list;
  - a list longer than the speaker enumerated.
- **Under A, additionally:** answered requests, poems, jokes, summaries, "include" → "exclude", swapped roles and objects, tense and modal changes, dropped prepositions and particles, inserted instructions.

**What is still rejected in every variant.** These are 5 Cerebras outputs and 5–6 Groq outputs, all from meaning checks misfiring on corrections:
- **`negationChanged` (6):** "what time is it no I mean what day is it", "that sounds pretty good no I mean that doesn't…", "…designed uh no let me start again…". The cue's "no" counts as a dropped negation.
  - A prototype that does not count "no" in "no I mean", "no wait" or "uh no let me" kept the adversarial count at 0, and all checks still passed.
  - Even so, these outputs then fail `unexplainedDeletion`. The replaced clause ("that sounds pretty good") lies outside the guard's correction window, which reaches back only over words the repair restates.
- **`numberDropped` (2):** "Send Alex 20 dollars no I mean do not send Alex 20 dollars" → "Do not send Alex 20 dollars." The first copy of "20" goes with the replaced clause.
- **`nameAdded` (2):** "shreia" → "Shreya" spelled from screen context, which the guard never accepts.

**Recommendation.** Do not drop the wording checks.
- Variant B gains 1 of 53 good outputs (Groq's U+2011 hyphen) for 11 adversarial passes. Variant A gains 2 and admits answers and meaning reversals.
- Several "wording" checks are really integrity checks, so keep them: no added symbols, a cue dropped while both versions are kept, and the list count.

What would help is making the meaning checks aware of corrections, in Professional only. This would recover up to 10 of the 12 remaining rejections. Each change must keep the adversarial count at 0.
1. **Don't count a cue's "no" as a negation.** The prototype above shows this is safe.
2. **Let an explicit cue ("no I mean", "wait no I meant", "let me start again") with a repair after it mark the whole replaced clause as droppable**, back to the previous sentence start, comma or "so/and/but". Droppable includes its numbers, names and negations. The output must still contain nothing that is not in the raw text after the cue, and must not keep both versions.
3. **Accept a screen-context name only when it respells a raw word spelled at least half like it.** This reuses the dictionary-term machinery and keeps context from ever adding a new name.

## Correction-aware guard

Following the recommendation above, Polish and Professional now understand explicit correction cues. Strict and Faithful do not change.

**What it accepts.**
- After "no I mean", "no, I mean", "wait no I meant", "actually no", "let me start again" or "scratch that", the guard also judges the output against the transcript with the cue and the clause before it removed (`CleanupGuard.applyExplicitRepairs`).
- The clause reaches back to the sentence start, a comma or "so/and/but", at most eight words. A leading "hey" or "um" stays.
- So the clause's numbers, names and negations may go, and the cue's "no" is never counted as a negation. Comma cues and most "scratch that" repairs, which are usually shorter than the clause, keep their existing handling.
- A screen-context spelling passes as a respelling of the raw word (see Screen context).

**What it still refuses.**
- The output is checked in full against the repaired text, so it cannot add anything the repair did not say, and it cannot keep both versions.
- The repair must restate one of the clause's content words within its first four words. It must also be at least as long as the clause, so a fragment never replaces a whole clause ("the report is not ready no I mean the slides" → "The slides." is refused).
- Only "no I mean" and "no I meant" may correct the sentence before them, across its period or question mark, since a speaker does not answer their own question with "I mean". So "What time is it? No, I mean, what day is it?" → "What day is it?" passes. A bare "No" after a question stays an answer: "Is the meeting today? No, it's tomorrow." → "It's tomorrow." is refused.
- Each number or name the clause loses needs one of its kind in the repair. "send 12 boxes wait no I meant send them friday" refines the boxes and does not replace them, so "Send them Friday." is still refused.

**Result**, re-scored offline with `--rescore` on the two saved Professional runs and with the cleanup corpus:

| Guard in Professional | Good live outputs accepted (Cerebras + Groq) | Adversarial outputs wrongly accepted |
|---|---:|---:|
| Before | 41 / 53 (21 + 20) | 0 |
| Correction-aware | **51 / 53** (26 + 25) | **0** at every level |

- All existing "refuses" checks still pass.
- New `check_cleanup` cases cover each new acceptance. Their must-fail counterparts are: a cue that keeps both versions, a repair that adds a fact, a negation dropped outside the correction, a fragment repair, and a name swapped for an unrelated or half-similar one, and a capitalized antonym ("include" → "Exclude").
- The two remaining rejections are Groq's new symbols (above), not corrections.
- **Limitation:** these are 10 recovered outputs from one run per provider. Polish was checked only with synthetic cases.

## Unsolved

- **A restarted sentence with no cue**, such as "The office was finished. The office was designed to…". Case 13 scores keeping both sentences as correct, because dropping the first can lose an independent fact ("The car is red. The car is parked outside."). A deterministic pre-pass would collide with "The project is late. The project is also over budget." It remains a model-only behaviour, and `gpt-oss-120b` keeps both.
- **Repairs the guard cannot place.** A cue-less repair, a repair shorter than the clause it replaces, or a clause over eight words with no comma or join still falls back with trust off. Trust AI rewrites is the supported path for them.
- **Polish and Strict**, other models, reasoning effort, and context on vs off still need live runs. Each newly compiled benchmark binary asks for Keychain access to the provider key, and the user was away to approve it.

## Pending live runs

Build once, approve the Keychain prompt for that binary, then run one provider at a time. Each command takes about 4–8 minutes.

```sh
pl="$(app/tools/build.sh check_professional_live)"   # called /tmp/pl below
# Cerebras (configured): levels, prompt variant, context on/off, new cases
/tmp/pl --runs 1 --mode polish --context on --trust on --delay 8 --retries 1 --json /tmp/cer-polish.json
/tmp/pl --runs 1 --mode strict --context on --trust off --delay 8 --retries 1 --json /tmp/cer-strict.json
/tmp/pl --runs 1 --prompt intent --mode professional --context on --trust on --delay 8 --retries 1 --json /tmp/cer-pro-intent.json
/tmp/pl --runs 3 --cases 19,22,23,24,25,32,33,34 --context off --trust on --delay 8 --retries 1 --json /tmp/cer-ctx-off.json
/tmp/pl --runs 3 --cases 19,22,23,24,25,32,33,34 --context on --trust on --delay 8 --retries 1 --json /tmp/cer-ctx-on.json
/tmp/pl --runs 1 --reasoning low --context on --trust on --delay 8 --retries 1 --json /tmp/cer-pro-low.json
/tmp/pl --runs 1 --model qwen-3.8-27b --context on --trust on --delay 8 --retries 1 --json /tmp/cer-qwen.json
# Groq (shares the transcription key's rate limit)
/tmp/pl --endpoint transcription --model openai/gpt-oss-20b --runs 1 --context on --trust on --delay 8 --retries 1 --json /tmp/groq-20b.json
/tmp/pl --endpoint transcription --model qwen/qwen3.8-27b --runs 1 --context on --trust on --delay 8 --retries 1 --json /tmp/groq-qwen.json
# Offline, no key: guard verdicts with trust off for any saved report
/tmp/pl --rescore /tmp/cer-polish.json
```

Adopt a variant for a level only if it scores at least as well as the production prompt and loses no must-keep case (15–18, 20) or context case.

## Method

- **Cases:** 35 hand-written dictations. Each lists the levels whose contract it tests. Professional runs 27 of them.
- **Scoring:** required and forbidden phrases, case-insensitive, with word boundaries. Non-list cases also forbid numbered lines. Phrase checks are transparent proxies, not semantic labels.
- **Configuration:** this branch's runs used the production `CleanupPrompt.build` with punctuation, spoken commands, fillers, corrections and lists on. They used `--runs 1 --delay 8 --retries 1`, on October 22, 2026. Cerebras and Groq ran at the same time on separate keys.
- **Isolation:** the benchmark reads only the endpoint, the model and its Keychain key. It uses an isolated `UserDefaults` suite and a temporary directory.
- **Data handling:** raw JSON reports stay outside the repository.

# Natural (local) — November 20, 2026

Natural on the managed Local model (Vedu Scribe 0.8B, installed Murmur 0.3.3 helper) under the guard, with the rule-based cleanup as fallback. Cleanup code on `main` (`2239a4f`) differs from tag v0.3.3 only in a comment in `app/Processing.swift`; `module-one` is unchanged, so `main` was measured.

- Device: MacBook Air, Apple M3, 16 GB, macOS 27.0.1.
- Tool: `app/tools/check_professional_live.swift` with `--endpoint local` (model + guard + fallback) and `--endpoint rules` (rules only); both with `--mode strict --context off --delay 0`. The same 24 hand-written cases that apply to Natural; model route 3 runs each (72), rules route 1 run (24, deterministic).
- Counts are against this tool's checks. A **hard** failure breaks a rule in the first list above (a lost "not", name, number or weekday, an added word); a **style** miss is anything else.

| Route | Runs | Hard failures | Style misses | Failed in total |
|---|---:|---:|---:|---:|
| Local model output, before the guard | 72 | 6 | 3 | 9 |
| Natural as shipped (model + guard + rule fallback) | 72 | 9 | 6 | 15 |
| Rules only | 24 | 5 | 3 | 8 |

- The guard held the model's output in 30 of 72 runs; the final text came from the rules in those runs, which is why the shipped route has more failures than the model alone.
- Rules-only hard failures: a lost "we won't", two weekday names, and two other changed words.
- **Not re-measured:** the 60 private real dictations. The replay set and its expected-text checks were not kept, and rebuilding them would mean reading History. The published "0 hard failures in 180 runs, guard fell back in 54 of 180" has no public evidence in this repository.
- Limits: 24 hand-written cases, one Mac, one model revision (`7dced58`). Raw reports are kept outside git.
