# Harness fixtures

23 short synthetic dictations used by the M2 exit harness (and later by M3's WER comparison).

- **Voice:** macOS `say -v Samantha` (US English, built-in synthetic voice), default rate.
- **Generated on:** macOS 27.0.1 (Darwin 27.0.0), `afconvert -f WAVE -d LEI16@16000 -c 1`.
- **Format:** 16 kHz, mono, PCM 16-bit WAV. About 2.3 MB in total; the longest clip is 7 s.
- **Content:** a short phrase, long sentences, numbers (order number, price, time), names, lists, questions, filler-heavy speech ("um so I think we should uh ..."), punctuation-dependent speech (quotes, semicolon, greeting) and mixed cases. All text is invented.

`phrases.tsv` is the source (`id`, spoken text, expected cleanup, tab separated). `generate.sh` rebuilds the WAVs and `manifest.json` on a Mac, and the output is deterministic. The WAVs are not committed, because the repository commits no audio (`.gitignore`, CONTRIBUTING.md). Copy them to the test machine before a run: `tar cf - *.wav | ssh <host> "cd /d <checkout>\windows\harness\fixtures && tar xf -"`.

`manifest.json` entries:

| field | meaning |
| --- | --- |
| `id` | fixture name, also the WAV file stem |
| `wav` | file name in this folder |
| `spoken` | the reference transcript (what was said, lower case, no punctuation). The fake provider returns this as the transcription. |
| `cleanup` | the expected cleanup output. The fake provider returns this from `/v1/chat/completions`. |
| `seconds` | WAV duration; the driver holds the key for this plus 0.4 s |

For M3's WER: the reference is `spoken` (normalize case and punctuation). Synthetic TTS is cleaner than real speech, so these numbers compare engines against each other, not against real-world accuracy.
