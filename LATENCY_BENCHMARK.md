# End-to-end latency — November 17, 2026

This November 17, 2026 measurement covers the time after the end of speech on one device, with synthetic speech. It is not a user study. See [MODEL_BENCHMARK.md](MODEL_BENCHMARK.md) for transcription accuracy and [README.md](README.md) for setup.

## Result

All times are milliseconds. "Median" and "P95" are over n = 20 clips per length (P95 is nearest rank, so with n = 20 it is the 19th-fastest value; treat it as an estimate). Insertion is not in the synthetic runs; the last table gives the app's own logged insertion timing from real use.

| Stage | Short (~5 s) median / P95 | Medium (~15 s) | Long (~45 s) |
|---|---:|---:|---:|
| Local ASR, Parakeet Unified, warm helper | 56 / 60 | 141 / 149 | 357 / 368 |
| Local rules cleanup (free, Strict) | 0 / 1 | 2 / 2 | 7 / 7 |
| Apple on-device cleanup, Polish | 1,019 / 1,091 | 1,924 / 2,055 | 4,301 / 4,552 |
| Apple on-device cleanup, Professional | 1,095 / 1,272 | 2,015 / 2,166 | 4,511 / 5,902 |
| Murmur Cloud transcription (upload + transcribe) | 776 / 2,008 | 1,134 / 3,499 (max 28,507) | 1,193 / 1,492 |
| Murmur Cloud cleanup, Polish (gpt-oss-20b) | 1,268 / 2,503 | 1,153 / 2,322 | 1,405 / 2,215 |

Cold start (one process launch or first request; n is small):

| Stage | Result |
|---|---|
| Local helper: new process to first answer (model load + a short clip) | 155 ms median of 3 (558 ms max). An earlier smoke run, the first launch after the model had sat idle, took 13.6 s (n = 1). |
| Apple on-device model, first call (short clip) | 1,919 ms (n = 1), against 1,019 ms warm |
| Murmur Cloud transcription, first request of the process (short clip) | 1,255 ms and 2,083 ms in two runs (n = 1 each), against 776–1,153 ms warm |

Local audio-prepare for cloud upload was 1–10 ms median (not included above).

Real use, from the app's own timing log (`app/tools/latency_report.sh 7d`; numbers only, 70 dictations on this Mac, local transcription with Murmur Cloud cleanup; these are not synthetic and not from History):

| Stage | p50 | p95 |
|---|---:|---:|
| Release to transcription start (release tail) | 465 | 472 |
| Cleanup | 478 | 2,249 |
| Insertion call | 36 | 88 |
| Text visible (insertion start to paste or read-back) | 36 | 83 |
| Whole dictation, release to text visible | 1,528 | 3,171 |

## Method

- Device: MacBook Air `Mac15,12`, Apple M3, 16 GB; macOS 27.0.1; Low Power Mode off. Thermal state and background load were not controlled; Murmur's own helper and other apps were running.
- Input: macOS `say` (default voice) rendered to 16 kHz mono WAV through `afconvert`. Twenty clips per length, each a different deterministic mix of 16 synthetic sentences with fillers ("um", "uh"), a spoken correction and numbers. Durations: short median 4.8 s (3.9–5.6), medium 15.3 s (14.6–16.4), long 44.9 s (44.1–46.2). Synthetic voices are cleaner than microphone speech; no text was printed or stored.
- Tool: `app/tools/measure_latency.swift`, built with `app/tools/build.sh measure_latency`. It uses the app's own `WarmRunner` with the installed ModelBench helper and the pinned Unified model, `ProcessingCoordinator` for cleanup (local rules, `AppleCleanupProvider`, `MurmurCloudCleanupProvider`), and `MurmurCloudTranscriptionProvider` for cloud transcription with the Keychain credential. It prints numbers only.
- Local ASR time is the warm helper round trip for a WAV path (file read, audio prepare, transcription), as the app does it. Cleanup time is `processAsync` (dictionary, rules, optional model call, guard, fallback). Cloud transcription time is the HTTPS upload plus transcription after the app's audio prepare.
- Cleanup stages ran on the local transcript of each clip (Murmur Cloud cleanup ran on the cloud transcript). Cleanup calls that raised a provider error are excluded and counted; calls where the guard rejected the model output and local rules took over are included in the time (the user waits for them) and counted in the last column of the tool output. Apple Polish rejected 5/12/11 of 20 clips (short/medium/long), Professional 8/10/12; Murmur Cloud 5/6/16. The synthetic corpus is dense in fillers and corrections, so these rates are not a prediction for real dictation.
- Run order: local ASR, local rules, Apple, then Murmur Cloud. Murmur Cloud numbers come from a second, paced run (2 s between requests), because the first, unpaced run returned errors for 13 of 20 medium and 8 of 20 long transcriptions. The tool did not record the error kind in that run, so the cause (rate limit, timeout or service) is not known. The paced run had 0 errors in 120 requests.
- The "release tail" (the app keeps recording about 0.4 s after key release) is not in the synthetic tables; the real-use table shows it as about 0.47 s.

## Interpretation and limits

- Local transcription is fast: a 45 s clip finishes in about 0.36 s, and 5 s in about 0.06 s. Local rules cleanup is negligible. Apple on-device cleanup is the slow local step and grows with length (about 1 s to 4.5 s).
- Murmur Cloud transcription is roughly 0.8–1.2 s median regardless of length, with a heavy tail (P95 up to 3.5 s, one 28.5 s outlier). Cloud cleanup is about 1.2–1.4 s median. Cloud cleanup is slower than the app's logged real-use cleanup median (0.48 s); the real use covers mostly short text and a warm connection, and the synthetic clips used the Polish level with 5–16 rejected outputs. Do not quote the synthetic cloud cleanup as the typical experience.
- Not measured: insertion on the synthetic clips (needs a focused app and Accessibility; the real-use log is the only source), a true first-ever launch (Core ML compile, about 14 s on first load), network conditions other than this one connection, dictionary boosting, Professional through Murmur Cloud, and the local free cleanup combined with an on-device Polish model other than Apple's. No other local Polish model route exists in the app besides a user-run local server.
- The totals are not sums of the medians. For an end-to-end figure use the real-use log, not the stage tables.

## Reproduce

```
export SDKROOT=/Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk
app/tools/build.sh measure_latency   # prints the executable path
[ML_N=20] [ML_COLD=3] <executable> HELPER_EXECUTABLE PARAKEET_MODEL_DIR [local,apple,cloud]
```

Murmur Cloud is skipped without a stored key. Keep cloud runs short.

## Website-safe claims

1. "On an M3 MacBook Air, local transcription finishes in about 0.06 s for a 5 s clip and about 0.36 s for a 45 s clip (median, warm, synthetic speech)."
2. "In 70 logged real dictations, the median time from key release to text appearing was about 1.5 s and the 95th percentile about 3.2 s (local transcription, Murmur Cloud cleanup, one Mac)."
3. Do not quote the cloud transcription or Apple cleanup figures, or any cold-start figure, as typical: they come from 20 synthetic clips on one connection and one Mac.
