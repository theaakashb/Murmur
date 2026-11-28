# Latency after speech — November 17, 2026

This November 17, 2026 measurement covers the time after the end of speech on one device, with synthetic speech. It is not a user study. See [MODEL_BENCHMARK.md](MODEL_BENCHMARK.md) for transcription accuracy and [README.md](README.md) for setup.

## Result

All times are milliseconds. "Median" and "P95" are over n = 20 clips per length (P95 is nearest rank, so with n = 20 it is the 19th-fastest value; treat it as an estimate). Insertion is not measured.

| Stage | Short (~5 s) median / P95 | Medium (~15 s) | Long (~45 s) |
|---|---:|---:|---:|
| Local ASR, Parakeet Unified, warm helper | 56 / 60 | 141 / 149 | 357 / 368 |
| Local rules cleanup (free, Strict) | 0 / 1 | 2 / 2 | 7 / 7 |
| Apple on-device cleanup, Polish | 1,019 / 1,091 | 1,924 / 2,055 | 4,301 / 4,552 |
| Apple on-device cleanup, Professional | 1,095 / 1,272 | 2,015 / 2,166 | 4,511 / 5,902 |

Cold start (one process launch or first request; n is small):

| Stage | Result |
|---|---|
| Local helper: new process to first answer (model load + a short clip) | 155 ms median of 3 (558 ms max). An earlier smoke run, the first launch after the model had sat idle, took 13.6 s (n = 1). |
| Apple on-device model, first call (short clip) | 1,919 ms (n = 1), against 1,019 ms warm |

## Method

- Device: MacBook Air `Mac15,12`, Apple M3, 16 GB; macOS 27.0.1; Low Power Mode off. Thermal state and background load were not controlled; Murmur's own helper and other apps were running.
- Input: macOS `say` (default voice) rendered to 16 kHz mono WAV through `afconvert`. Twenty clips per length, each a different deterministic mix of 16 synthetic sentences with fillers ("um", "uh"), a spoken correction and numbers. Durations: short median 4.8 s (3.9–5.6), medium 15.3 s (14.6–16.4), long 44.9 s (44.1–46.2). Synthetic voices are cleaner than microphone speech; no text was printed or stored.
- Tool: `app/tools/measure_latency.swift`, built with `app/tools/build.sh measure_latency`. It uses the app's own `WarmRunner` with the installed ModelBench helper and the pinned Unified model, and `ProcessingCoordinator` for cleanup (local rules, `AppleCleanupProvider`). It prints numbers only.
- Local ASR time is the warm helper round trip for a WAV path (file read, audio prepare, transcription), as the app does it. Cleanup time is `processAsync` (dictionary, rules, optional model call, guard, fallback).
- Cleanup stages ran on the local transcript of each clip. Cleanup calls that raised a provider error are excluded and counted; calls where the guard rejected the model output and local rules took over are included in the time (the user waits for them) and counted in the last column of the tool output. Apple Polish rejected 5/12/11 of 20 clips (short/medium/long), Professional 8/10/12. The synthetic corpus is dense in fillers and corrections, so these rates are not a prediction for real dictation.
- Run order: local ASR, local rules, then Apple.
- The "release tail" (the app keeps recording about 0.4 s after key release) is not in the tables.

## Interpretation and limits

- Local transcription is fast: a 45 s clip finishes in about 0.36 s, and 5 s in about 0.06 s. Local rules cleanup is negligible. Apple on-device cleanup is the slow local step and grows with length (about 1 s to 4.5 s). Apple Intelligence is not a cleanup choice in the app; it runs writing modes and voice edit on local routes, so these rows show that engine's speed on this text.
- Not measured: insertion (needs a focused app and Accessibility), a true first-ever launch (Core ML compile, about 14 s on first load), dictionary boosting, a Custom Provider, and the Local model (Murmur Cleanup 2B), whose latency is on its [model card](https://huggingface.co/clouddevaakash/murmur-cleanup-2b).
- The stage tables are not an end-to-end figure; do not add their medians.

## Reproduce

```
export SDKROOT=/Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk
app/tools/build.sh measure_latency   # prints the executable path
[ML_N=20] [ML_COLD=3] <executable> HELPER_EXECUTABLE PARAKEET_MODEL_DIR [local,apple]
```

## Website-safe claims

1. "On an M3 MacBook Air, local transcription finishes in about 0.06 s for a 5 s clip and about 0.36 s for a 45 s clip (median, warm, synthetic speech)."
2. Do not quote the Apple cleanup figures, or any cold-start figure, as typical: they come from 20 synthetic clips on one Mac.
