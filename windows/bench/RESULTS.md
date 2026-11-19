# Local speech recognition on Windows: measurements (M3)

Synthetic speech, 23 clips, 70.2 s, CPU only. Measured 2026-11-19 with `MurmurSpeech` (sherpa-onnx 2.0.1, ONNX Runtime 1.28.2) running `nvidia/parakeet-unified-en-0.6b` as the int8 build `csukuangfj2/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming@8c3a10f`. Reproduce with [README.md](README.md). Limits are listed at the end; read them before quoting a number.

## Accuracy and latency

| Engine | Micro WER | Word edits / reference words | Exact clips | WER without the 4 clips where an engine wrote digits |
|---|---:|---:|---:|---:|
| Mac Core ML (FluidAudio, the Mac app's `ModelBench`) | 8.20% | 20 / 244 | 16 / 23 | 1.49% (3 / 201) |
| Mac ONNX (this helper, Apple M3, CPU) | 9.02% | 22 / 244 | 15 / 23 | 1.99% (4 / 201) |
| VM ONNX (this helper, Windows 11, CPU) | 9.02% | 22 / 244 | 15 / 23 | 1.99% (4 / 201) |

The references are the spoken words, so "4721" for "four seven two one" counts as errors for every engine; the last column drops the four clips where any engine wrote digits. The ONNX build made one more word error than Core ML on those 19 clips (`list-tasks`: "then" for "and", with commas the Core ML text lacks), and dropped the word "the" in `numbers-price`. The Mac and the VM produced the same text on all 23 clips in these runs (they did differ on two of the 12 spike clips below, so CPU kernels can change a word at the margin).

| | Load (model in memory) | First transcribe in a fresh process (`--single`) | Warm request (`--serve`, wall clock through the app's `WarmRunner`) |
|---|---|---|---|
| Mac Core ML | 10.41 s the first time (Core ML compile), then 0.08 s | 58 ms median, 76 ms max | n/a: the baseline file is one process per clip |
| Mac ONNX (M3, 8 logical CPUs, 16 GB, macOS 27.0.1) | 0.80 s median (max 1.06); warm helper ready in 0.86 s | 110 ms median, 218 ms max; whole one-shot process 0.99 s median | 123 ms median, 239 ms max |
| VM ONNX (QEMU Virtual CPU 2.5+, 16 vCPUs, 10 GB, Windows 11 23H2 10.0.22631) | 1.84 s median (max 1.95); warm helper ready in 1.94 s | 353 ms median, 744 ms max; whole one-shot process 2.39 s median | 359 ms median, 731 ms max |

Clips average 3.05 s. Warm numbers are pass 2 of two passes over the 23 clips. `--single` text equalled `--serve` text on 23 / 23 clips on both machines. An earlier run of the same code on the VM gave 2.69 s load and 563 ms median warm, so VM numbers move by about half between runs; the Mac moved by about 20%.

Helper memory on the Mac after one request: 1.18 GB resident (about 0.65 GB of it the int8 encoder weights); 1.96 GB with dictionary support, which keeps a second recognizer (greedy for the raw text, beam search with hotwords for the boosted text). Not measured on Windows.

## Dictionary support: hotwords spike

Question: do sherpa-onnx hotwords (`modified_beam_search`) plus the ported `BoostFilter` fix the spelling of made-up names without changing ordinary words?

Setup: 8 clips with 9 made-up name mentions (`hotwords-phrases.tsv`), 4 "trap" clips of ordinary words that sound a little like dictionary terms, plus the 23 fixtures. The vocabulary held the 7 made-up names plus three real words, "Notion", "Gemini" and "Planit", as bait. The lexicon for `BoostFilter` was macOS's `/usr/share/dict/words` (a stand-in; the bundled word list does not exist yet). Mac, helper default of 4 threads.

| Configuration | Names spelled right (of 9) | Name clips: word edits | Trap clips: word edits | 23 fixtures: word edits | Clips where ordinary words changed |
|---|---:|---:|---:|---:|---:|
| no vocabulary | 4 | 5 | 0 | 22 | 0 |
| hotwords 1.5 + BoostFilter | 6 | 3 | 0 | 22 | 0 |
| hotwords 3 + BoostFilter | 6 | 3 | 0 | 22 | 0 |
| hotwords 5 + BoostFilter | 5 | 5 | 0 | 22 | 0 |
| hotwords 1.5, before BoostFilter | 6 | 3 | 0 | 22 | 1 |
| hotwords 3, before BoostFilter | 6 | 5 | 0 | 22 | 1 |
| hotwords 5, before BoostFilter | 7 | 276 | 170 | 1049 | 27 |

What the 3 / 3 / 22 / 0 pattern means:

- At score 1.5 (sherpa-onnx's default), `Brindluick` became `Brindlewick` and `Quilfeather` became `Quillfeather`, in two clips each; `Zorbax`, `Vandermeer Tolliver` and `Fenwhistle` were already right or stayed right. No ordinary word changed in any of the 4 trap clips or the 23 fixtures. The VM gave the same direction: 3 of 9 right without, 6 of 9 with.
- It also broke one name that was right: `Kestrelmoor` came out as `Kestrilmor` at scores 1.5 and 3 (on both machines), and BoostFilter keeps that replacement, because the replaced word is not an ordinary word and the spelling is similar. Two names stayed wrong (`Andrasek` for `Ondrasek`; `Quillfather` for `Quillfeather` in the single-name clip, closer but not right).
- Net: +2 names of 9 on this set, one regression, no ordinary-word damage. That is a small, synthetic sample.
- Before BoostFilter, score 3 inserted "Gemini Gemini" into a clip and score 5 produced runaway repeats of the vocabulary words ("Ondrasek Notion Notion ..."). BoostFilter blocked the insertion at 3 and, at 5, kept the raw text, which is the filter doing its job. Scores above about 3 are not usable with 10 terms.
- Cost: a second decode and a second recognizer. Mac warm median 123 ms to 294 ms; VM 359 ms to 782 ms; load 0.8 s to 1.8 s (Mac) and 1.8 s to 3.9 s (VM); resident memory 1.18 GB to 1.96 GB.
- The hotword spellings are tokenized by sherpa-onnx's BPE encoder over a vocabulary file generated from `tokens.txt` with equal scores, because the model folder has no SentencePiece model. Real piece scores could change which names get fixed.
- The helper only loads dictionary support when the app finds `Speech\english-words.txt` next to the helper; without a word list `BoostFilter` would treat every word as ordinary and the second decode would change nothing, so the app does not pay for it. The word list is not part of this work.

Verdict: hotwords with the filter are safe for ordinary words and give a small gain on made-up names, but they are not the same strength as the Mac's CTC rescoring (not measured here against the Mac on this set). Whether that is enough is for the user to decide; the next candidate in PLAN.md is porting the CTC rescoring.

## Limits

- Synthetic text-to-speech (`say` voice Samantha), one voice, clean audio: not accents, noise, a real microphone or spontaneous speech.
- 23 clips and 244 reference words: one word is 0.4 percentage points. The differences between engines here are one or two words and are not statistically meaningful.
- CPU only. The VM is a QEMU virtual CPU with no GPU, so its latencies say little about a real PC; the Mac figures are for an M3.
- The Core ML baseline is one process per clip (`mac-asr.jsonl`), so it has no warm column; its load figure includes a Core ML compile the first time.
- The Core ML run used FluidAudio's own preprocessing and conversion (its quantization was not checked); the ONNX run is the int8 build. The int8 build's WER against the full-precision `…-non-streaming@e69c9fb` was not measured.
- Not measured: peak memory on Windows, long recordings, `--stream` latency and live-word quality, `{"timed"}` word-time accuracy.
