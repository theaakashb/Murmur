# Local cleanup model against gpt-oss-120b

The shipped local cleanup model, Murmur Cleanup 2B revision 5, was compared with the cloud model gpt-oss-120b on a fresh held-out set of 290 rows, run on an Apple M3 at 4-bit. Results, from its [model card](https://huggingface.co/clouddevaakash/murmur-cleanup-2b):

- Blind pairwise preference for the local model: 61% overall (Polish 63%, Professional 67%).
- Hard failures: 3.8%, against 4.1% for gpt-oss-120b.
- Screen-context bleed: 0%.
- Latency: p50 0.62 s, p95 1.70 s.

The card has the method and the full results.

# Local model on Windows (GGUF via llama.cpp) vs Mac (MLX) — November 19, 2026

Do the Vedu Scribe 0.8B weights, run through LLamaSharp 0.30.0 (llama.cpp CPU backend) as `windows/src/Murmur.Cleanup`, behave like the Mac's MLX `LocalCleanupRunner`? Both helpers speak the same protocol with the same prompt (system `Clean up this dictation.`, no header because the corpus has no tone or vocabulary, thinking off, greedy, 400-token input cap, 512 output tokens).

**Method.** The 400 distinct inputs of `app/cleanup-corpus.json` (426 cases) went one request at a time through each helper (`windows/bench/cleanup/run_corpus.py`); `windows/bench/cleanup/CorpusReport` compares outputs with the MLX output character for character and runs Core's `CleanupGuard` on each, strict (the case's own options, Polish, Professional and trust off, as the Mac applies to the local model). Raw outputs stay out of git. GGUFs are `convert_gguf.py` output with the layer count of the header fixed (`module-one/cleanup/patch_gguf_layers.py`; the converter's file does not load in LLamaSharp's llama.cpp, see `module-one/cleanup/README.md`). Mac: Apple M3, CPU only (`GpuLayerCount = 0`) for the GGUF runs, MLX on its usual path. Windows: a Windows 11 virtual machine (QEMU Virtual CPU, 16 logical processors, 10 GB), driven over ssh from the Mac, so its timings include a few milliseconds of network.

**Token IDs.** 1,066 strings (every corpus input as the plain string the 400-token cap counts and as the full ChatML prompt, plus headers and edge cases: non-ASCII, emoji, newlines, long runs) tokenized by the checkpoint's Hugging Face tokenizer and by the GGUF: **1,066 identical, 0 mismatches** (`tokenizer_parity.py`). The Mac runner's `too-long` boundary agreed too: on 17 formal-header inputs of 150 to 390 words both returned an answer up to 375 words and `too-long` at 390.

| Run | Exact match vs MLX | Strict guard pass | `too-long` / failed | Load | Median / p90 / max per request |
|---|---:|---:|---:|---:|---:|
| Mac, MLX (baseline) | 400 / 400 | 321 / 400 | 0 / 0 | 1.1 s | 118 / 177 / 306 ms |
| Mac, GGUF F16 | 400 / 400 | 321 / 400 | 0 / 0 | 0.8 s | 442 / 637 / 1,271 ms |
| Mac, GGUF Q8_0 | 399 / 400 | 320 / 400 | 0 / 0 | 1.3 s | 208 / 332 / 647 ms |
| Windows VM, GGUF Q8_0 | 399 / 400 | 321 / 400 | 0 / 0 | 1.1 s | 1,313 / 1,963 / 4,042 ms |

Differences from MLX, each one request:

- Mac Q8_0, `combo-003` ("The call is at, um, four, sorry, five."): "…at 5." instead of "…at five."; the strict guard rejects it.
- Windows VM Q8_0, `combo-005` ("So, um, Monday, no, Tuesday works best."): "Tuesday works best." instead of "So, Tuesday works best."; the guard accepts it. The Mac's Q8_0 gives the MLX answer here, so this is floating-point drift between CPUs (the VM's CPU model hides AVX2 [Guessing]; the SIMD path differs), not a conversion fault.

MLX's own strict guard pass rate is 321 of 400, so the bar is that rate, not 100 %.

**Reading it.** F16 reproduces MLX exactly on this Mac, which confirms the conversion and the prompt; it costs 1.5 GB and about twice Q8_0's time. Q8_0 (812 MB) differs on 1 of 400 inputs on each machine, and the guard pass rate on the Windows VM equals MLX's. That meets the bar in `windows/PLAN.md` (guard pass rate within a small, stated margin of the Mac's; every difference listed). **Recommendation: ship Q8_0.** F16 stays the fallback if a later corpus shows Q8_0 drift that the guard lets through.

**Limits.** One run per configuration; greedy decoding is deterministic on one machine, not across CPUs. The corpus is short synthetic text (most inputs well under 100 tokens), has no tone or vocabulary headers, and its `expected` column is not used here, so this measures agreement with the Mac, not quality. The Windows numbers come from one virtual machine with software-emulated CPU features: expect real hardware to be faster (AVX2, more cache) and its Q8_0 output to track the Mac's more closely; neither is measured. Latency was measured one request at a time with the model warm; the VM median of 1.3 s (max 4 s) should be re-measured on a physical PC before the Windows default is chosen. The MLX tokenizer reference is the Hugging Face tokenizer; the Mac runner's Swift tokenizer was checked only through the `too-long` boundary. Text containing a literal `<|im_end|>` is parsed as a control token on both platforms; not tested.

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
