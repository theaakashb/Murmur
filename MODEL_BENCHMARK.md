# Local transcription comparison — October 6, 2026

This October 6, 2026 comparison measures clean read speech on one device. It does not establish app release readiness. See [README.md](README.md) for setup.

## Result

On this initial read-speech sample, the current Parakeet Unified model had the lowest word error rate and matched Parakeet V2's inference speed. Keep Unified as the current choice for now; this small clean-speech comparison is not enough to claim it is best for spontaneous dictation.

| Model | Pinned artifact | Micro WER | Macro WER | Word edits / reference words | Exact clips | Median ASR time | P95 ASR time | Transfer size |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| Parakeet Unified English 0.6B (current) | `FluidInference/parakeet-unified-en-0.6b-coreml` @ `d32e972dd4315f1dc3f6be28fb2aab0ab3e80358` | **1.34%** | **1.74%** | 29 / 2,172 | 79 / 100 | 62 ms | 149 ms | 1.802 GB |
| Parakeet TDT V2 English 0.6B | `FluidInference/parakeet-tdt-0.6b-v2-coreml` @ `ee09c569f73759e6d44c9bd16766f477b2b36d39` | 1.93% | 2.01% | 42 / 2,172 | 77 / 100 | **61 ms** | **100 ms** | 464 MB* |
| Whisper large-v3 Turbo (WhisperKit Core ML) | `argmaxinc/whisperkit-coreml` @ `0f63a7800b00dd0226abd051b906c246e1907482` | 2.12% | 2.49% | 46 / 2,172 | 76 / 100 | 1.89 s | 3.98 s | 3.195 GB |

Unified made 13 fewer word errors than V2 and 17 fewer than Whisper on these 2,172 words. V2's median ASR time was effectively tied with Unified; Whisper's was about 31 times longer.

First process load times were 13.93 s for Unified, 0.27 s for V2, and 208.79 s for Whisper. Do not compare these as cold starts: V2's Core ML compilation had already occurred during a failed first scorer startup, while Whisper's run included first-use compilation.

\* V2 size is the required four Core ML bundles plus vocabulary, not the whole repository, which contains additional variants. Transfer sizes are model artifact bytes, not memory use.

## Method

- Device: MacBook Air `Mac15,12`, Apple M3, 16 GB RAM; macOS 26.6.2 (25G83).
- Dataset: LibriSpeech `test-clean`, licensed CC BY 4.0. The deterministic sample used 100 clips distributed round-robin over all 40 test-clean speakers (seed `10052026`): 13.50 minutes and 2,172 reference words. The clips are clean read audiobook speech, not spontaneous speech or user dictation. [Dataset source](https://www.openslr.org/12)
- All three runners processed the same 16 kHz mono WAV files and emitted raw ASR text; no dictionary, cleanup, or insertion stage ran.
- WER lowercases and tokenizes with `\w+(?:['’]\w+)?`; punctuation is ignored, but spoken numbers and digit forms are not normalized to one another. Results use micro WER across all reference words; exact clips and macro WER are supplemental.
- ASR timings are measured inside each long-lived process after its model is loaded. Unified/V2 report transcription time after their explicit audio conversion; WhisperKit's API timing includes its file loading/conversion. These are backend timings, not end-to-end dictation latency.
- Runs were sequential, once per model, in Unified → V2 → Whisper order. Thermal state, power mode, system pressure, ANE utilization, and peak memory were not controlled or measured. Murmur's idle model helper was also running.
- Unified and V2 used FluidAudio revision `0b1f46289fe27d95b5e66ad8be46e64f5ee02ae7`. WhisperKit used commit `a8a40c9444ae4e7263bd41e5e60b35a92e2cbba3` (tag `v0.9.0`), model files at the pinned revision above, and tokenizer metadata from `openai/whisper-large-v3` revision `06f233fe06e710322aca913c1bc4249a0d71fce1`.
- Raw audio, references, transcripts, and per-clip outputs remain in the private cache at `~/Library/Caches/Dictation/asr-benchmark/`; they were not added to the repository.

## Interpretation and limits

This first pass favors Unified on WER without a measurable inference-time penalty against V2 on the sample. It does not validate live mic capture, accents beyond this dataset's speakers, noise, names/numbers in realistic dictation, short user commands, or Murmur's end-to-end path.

Model weights are downloaded separately. See the app’s pre-download disclosure and [third-party notices](THIRD_PARTY_NOTICES.md) for the differing license declarations before downloading or redistributing weights.

The runner additions are benchmark-only; the installed app and its default model were not changed.

## Windows (ONNX) vs Mac (Core ML), synthetic speech

Added November 19, 2026. This is a different, much smaller test than the one above: 23 synthetic dictations (macOS `say`, voice Samantha, 16 kHz mono, 70.2 s, 244 reference words), not LibriSpeech. It checks that the Windows helper runs the same model close to the Mac's accuracy; it does not rank models. Scripts, exact commands and the full tables are in [windows/bench](windows/bench/RESULTS.md).

| Engine | Micro WER | Word edits / reference words | Exact clips | Warm request, median / max |
|---|---:|---:|---:|---:|
| Mac Core ML (FluidAudio), Apple M3 | 8.20% | 20 / 244 | 16 / 23 | n/a (one process per clip; 58 ms / 76 ms first transcribe) |
| Mac ONNX (sherpa-onnx int8, CPU), Apple M3 | 9.02% | 22 / 244 | 15 / 23 | 123 ms / 239 ms |
| Windows ONNX (the same helper, CPU), QEMU virtual CPU, 16 vCPUs, 10 GB, Windows 11 | 9.02% | 22 / 244 | 15 / 23 | 359 ms / 731 ms |

- WER uses `benchmark.py`'s normalization against the spoken words. Every engine writes numbers as digits, so four clips cost errors for all of them; without those clips Core ML made 3 word errors in 201 words and the ONNX helper 4.
- Load: Core ML 10.4 s the first time and 0.08 s after; ONNX 0.8 s (Mac) and 1.8 s (VM) for the helper process to be ready. These are helper loads, not app launch.
- The Mac and the VM produced the same text on all 23 clips. Two of 12 other clips differed between them, so a different CPU can change a word at the margin.
- The ONNX run is sherpa-onnx's int8 build (`csukuangfj2/sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming@8c3a10f`); the Core ML run is FluidAudio's own Core ML conversion, whose quantization was not checked.
- Limits: synthetic speech with one voice and no noise; 23 clips, where one word is 0.4 points, so a one- or two-word gap is not a finding; CPU only; the Windows machine is a virtual CPU whose latency says little about a real PC, and it varied by about half between two runs; memory on Windows was not measured; not run: accents, real microphones, long recordings, live words, speaker timing.
