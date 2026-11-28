# Local cleanup model against gpt-oss-120b

The shipped local cleanup model, Murmur Cleanup 2B revision 5, was compared with the cloud model gpt-oss-120b on a fresh held-out set of 290 rows, run on an Apple M3 at 4-bit. Results, from its [model card](https://huggingface.co/clouddevaakash/murmur-cleanup-2b):

- Blind pairwise preference for the local model: 61% overall (Polish 63%, Professional 67%).
- Hard failures: 3.8%, against 4.1% for gpt-oss-120b.
- Screen-context bleed: 0%.
- Latency: p50 0.62 s, p95 1.70 s.

The card has the method and the full results.

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
