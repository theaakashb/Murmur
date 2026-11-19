# Murmur for Windows: plan

Status: Phase 1 (planning). No Windows code exists yet. This plan was written on a Mac; nothing in it has been built or run on Windows. Claims about Windows APIs and third-party libraries are marked **(verify)** where they have not been checked against the installed version.

## Decisions already made

- Native C#/.NET app in this repo under `windows/`. The Mac app stays Swift.
- V1 is a full port: hold-to-talk dictation, cleanup and its routes, Murmur Cloud, dictionary and learning, writing modes, voice edit, History and retention, settings, local speech recognition at launch, and meeting capture (microphone plus system audio, call detection, speaker separation).
- Insertion is paste only: clipboard plus Ctrl+V, with a paste-last-dictation hotkey. No typing fallback and no "couldn't confirm" badge (matches the Mac after PR #94).
- Signing: Azure Artifact Signing (formerly Trusted Signing) or an Authenticode certificate, bought by the user. Real testing on the user's Windows PC; a Windows VM can serve CI and signing.
- Decided after Phase 1 review (2026-11-18): the same local cleanup model and the same speech model as the Mac; a bundled English word list as the lexicon; Right Ctrl hold-to-talk and Ctrl+Win+Shift+V paste-last (Win+Ctrl+V is likely taken by Windows 11's sound-output panel), with the welcome guide detecting a missing Right Ctrl (Copilot-key laptops) and offering to pick another key; the cleanup GGUF hosted in a Hugging Face repository under the user's account (files and pin prepared here; creating or uploading needs the user's go-ahead); NVIDIA's Open Model License treated as governing the Parakeet ONNX conversion; Azure Trusted Signing (now named Azure Artifact Signing); Windows 11 22H2+ x64 first, ARM64 later; website matching for modes is required in v1; learning from edits is required; GitHub releases only; an in-app updater is required.
- Same product rules as the Mac: replace, don't keep fallbacks; transcription routes stay fixed on failure; the guarded local cleanup fallback stays; never log transcripts, audio, keys or response bodies.

## Source of truth

The Swift code is the spec. The Windows app reproduces its behavior, not its structure. Where this plan and the Swift code disagree, the Swift code wins and this plan is fixed.

- [spec/pipeline.md](spec/pipeline.md): one dictation from key down to History, voice edit and paste-last, with Swift file:line references.
- [spec/settings.md](spec/settings.md): every stored setting, its default, credentials and on-disk files.
- Golden data shared with the Mac, read in place (never copied): `app/cleanup-corpus.json` (426 cases), `app/meeting-notes-corpus.json` (22 meetings), `app/meeting-notes-heldout.json` (9).
- The Swift checks (`app/check_*.swift`, about 2,100 assertion lines) are the rest of the behavioral spec. Each C# test file names the Swift check it ports.

## Architecture

```
windows/
  Murmur.Windows.slnx
  Directory.Build.props        shared settings: net10.0, nullable, warnings as errors
  src/Murmur.Core/             net10.0, no Windows APIs: everything Foundation-only on the Mac
  src/Murmur.App/              net10.0-windows: WinUI 3 shell plus the Windows platform layer
  src/Murmur.Speech/           net10.0 console helper: local ASR and diarization, the ModelBench line protocol
  src/Murmur.Cleanup/          net10.0 console helper: the local cleanup model, the LocalCleanupRunner line protocol
  tests/Murmur.Core.Tests/     xUnit, runs on Windows, Linux and macOS
  spec/                        written specs derived from the Swift code
```

Five projects, no more until a concrete need appears. Platform code (audio, hotkeys, clipboard, UI Automation, credentials) lives in `Murmur.App` under a `Platform/` folder rather than its own project; it can only be tested on Windows anyway.

**Murmur.Core** ports the roughly 60 Foundation-only Swift files that `app/check.sh` already builds in isolation: `CleanupTypes`, `CleanupText`, `CleanupGuard`, `CleanupPrompt`, `Cleanup`, `RuleCleaner`, `Dictionary`, `Learning`, `Processing`, `Retention`, `History`, `PrivateFiles` (behavior only), `Routes`, `ProviderTypes`, `ProviderHTTP`, `OpenAICompatible`, `MurmurCloud`, `MurmurCloudEdit`, `EditPrompt`, `WritingModes`, `VoiceCues`, `Meetings`, `MeetingStore`, `MeetingExport`, `MeetingNotes`, `MeetingSpeakers`, `CallApps`, `CallDetection`, `ModelManifest`, `ModelDownload`, `Digests`, `WordDiff`, `Insights`, `Tips`, `BoostFilter`. It builds and tests on this Mac, Linux CI and Windows. The Windows-specific lexicon and clock come in through interfaces, as `isCommonWord` does in Swift.

**Murmur.App** is the tray app: tray icon and menu, the recording bubble, Settings, History, Meetings, the welcome guide, and the platform layer.

**Murmur.Speech** is a separate process for the same reasons ModelBench is one on the Mac: a native-library crash or a stuck model cannot take down the app, memory goes back to the OS when it exits, and the app side (warm runner, chunk jobs, timeouts, one-shot fallback) can be ported one to one from `app/ModelHelper.swift`. It speaks the same line protocol as `module-one/models/Runner/Sources/ModelBench/ModelBench.swift`: `--serve` (one JSON line per request: an audio path, `{"samples":BASE64}`, `{"timed":PATH}`, `{"boundary":PATH}`), `--stream RATE` (`{"partial":…}`), `--diarize KEPT_WAV THRESHOLD` (`{"segments":[…]}`), with `{"ready":true}` first and `<unk>` scrubbed from every line.

**Murmur.Cleanup** runs the local cleanup model in its own process, as `LocalCleanupRunner` does on the Mac (`module-one/cleanup/Runner/Sources/LocalCleanupRunner/main.swift`): one JSON line per request, `{"id","text","tone","terms"}` in, `{"id","text"}` or `{"id","error":"too-long"|"failed"}` out, `{"ready":true}` first; one request at a time with a fresh chat; exits when idle and at quit; cancel or timeout kills only the child. See "Local cleanup model".

### UI framework: WinUI 3

Recommendation: **WinUI 3** (Windows App SDK), unpackaged, self-contained.

- It gives the Windows 11 look (Fluent controls, Mica, system accent, light/dark) that the Mac app's design standard (`app/DESIGN.md`) implies. WPF would need a third-party theme to look current.
- The parts that are hard in either framework are the same Win32 work: a topmost, no-activate, click-through bubble window (`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT`), the tray icon, the low-level keyboard hook. So WPF's maturity buys little here.
- Cost: WinUI 3 cannot be built on macOS or Linux; its XAML compiler runs only on Windows **(verify)**. WPF can be compiled elsewhere with `EnableWindowsTargeting`. Since all logic lives in `Murmur.Core`, which builds anywhere, the shell being Windows-only to build is acceptable; Windows CI catches shell build breaks.
- Risk: WinUI 3 has a history of rough edges (tray support needs a helper such as H.NotifyIcon, window-sizing APIs are thinner than WPF). If milestone M2's shell spike hits a blocker, switch to WPF at that point, before much UI exists.

### Runtime and versions

- .NET 10 (LTS, released November 2025, supported to November 2028) rather than .NET 11 (STS) **(verify support dates)**.
- Windows App SDK 1.8 or later, self-contained so users need no runtime install **(verify current version)**.
- Libraries, each to be license-checked and added to `THIRD_PARTY_NOTICES.md` before use: NAudio (MIT) for WASAPI capture, sherpa-onnx (Apache-2.0) with ONNX Runtime (MIT) for ASR and diarization, LLamaSharp (MIT) with its llama.cpp backend (MIT) for the local cleanup model, Velopack (MIT) for install and updates, H.NotifyIcon (MIT) for the tray. No other dependencies without a reason.

## Subsystem map

| Subsystem | Mac source of truth | Windows approach |
| --- | --- | --- |
| Hold-to-talk shortcut | `Shortcut.swift` (`ShortcutMachine`: hold, double-press toggle, short-tap cancel), event tap thread | Same state machine in Core. Input from `WH_KEYBOARD_LL` hook on its own thread (needed for modifier-only keys and key-up). Presets differ: Right Alt is AltGr on many layouts, so the default is Right Ctrl hold-to-talk (decided); the welcome guide detects a keyboard without Right Ctrl (Copilot key) and asks for another key. |
| Recording | `Capture.swift`, `Session.swift`, `AudioFormat.swift` | WASAPI shared-mode capture via NAudio, converted to 16 kHz mono Float32. Same min-length, silence and chunking rules (`Chunker`). |
| Local ASR | `ModelHelper.swift`, `ModelBench.swift`, `ModelFiles/ModelFlow/ModelManifest/ModelDownload.swift`, `artifact-pins.json` | `Murmur.Speech` on sherpa-onnx. See "Speech recognition". |
| Cloud transcription | `Routes.swift`, `CloudAudio.swift`, `OpenAICompatible.swift`, `MurmurCloud.swift`, `ProviderHTTP.swift` | `HttpClient` port in Core. Same endpoints, payloads, validation, redirect, size and timeout rules. No dictionary prompt. |
| Dictionary correction and lexicon | `Dictionary.swift`, `Processing.swift`, `BoostFilter.swift`; lexicon = `NSSpellChecker` (`Controller+Dictionary.swift:92`) | Core port. Lexicon on Windows: a bundled English word list (decided), so results are the same on every PC and in CI; it will differ from `NSSpellChecker` at the edges, and the list and its license are pinned like a model. |
| Cleanup | `Cleanup.swift`, `RuleCleaner.swift`, `CleanupPrompt.swift`, `CleanupGuard.swift`, `CleanupText.swift` | Core port, validated by `cleanup-corpus.json` and `check_cleanup`/`check_processing`. Regex rewrite notes below. |
| Writing modes and AI engine | `WritingModes.swift`, `AIEngine.swift`, `ScreenReading.swift` (browser URL, visible text) | Core port. App matching by process name; website matching is required, see "Website matching for modes". Apple Intelligence engine has no equivalent: Custom Provider or Murmur Cloud only. |
| Voice edit | `VoiceEdit.swift`, `EditPrompt.swift`, `EditTrigger.swift`, `EditShortcut.swift`, `MurmurCloudEdit.swift` | Core port. Selected text via UI Automation `TextPattern`; never from a password field. |
| Insertion | `Insertion.swift`, `Delivery.swift` | Clipboard stage, `SendInput` Ctrl+V, delayed restore of the previous clipboard only if it still holds our item. See "Insertion" below. |
| Learning from corrections | `Learning.swift`, `Controller+Learning.swift`, `Observation.swift`, field read-back in `Insertion.swift` | Core port. Required in v1; see "Learning from edits". |
| History and retention | `History.swift`, `Retention.swift`, `PrivateFiles.swift`, `HistoryRouter.swift` | Core port. Files under `%LOCALAPPDATA%\Murmur` (not Roaming, so transcripts never sync), owner-only protected DACL, atomic replace. |
| Credentials | `Credentials.swift` (Keychain) | Windows Credential Manager generic credentials (`CredWriteW`, DPAPI per user). Same destination binding of keys. |
| Settings | `Settings.swift`, `Controller+Settings.swift`, many per-feature keys | One JSON settings file in the private folder, same key names and defaults as `spec/settings.md`. |
| Meetings | `Meetings.swift`, `MeetingCapture.swift`, `MeetingRecorder.swift`, `MeetingStore.swift`, `MeetingNotes.swift`, `MeetingExport.swift`, `MeetingSpeakers.swift`, `MeetingsUI.swift` | See "Meeting capture". |
| Call detection | `CallDetection.swift`, `CallApps.swift`, `CallOfferUI.swift`, `AudioProcessList` in `MeetingCapture.swift` | Same detector in Core; input from WASAPI audio session enumeration (`IAudioSessionManager2`, per-process capture/render activity) and a Windows call-app table (Teams, Zoom, Slack, Discord, Webex, browsers by process name). |
| Voice cues | `VoiceCues.swift` | Core port; sounds through WASAPI/`MediaPlayer`. |
| Tips, insights, welcome guide | `Tips.swift`, `Insights.swift`, `WelcomeGuide*.swift`, `ProfessionalHint*.swift` | Core logic port; WinUI views. Guide steps rewritten for Windows permissions (there is no Accessibility permission; microphone privacy toggle instead). |
| Diagnostics, report issue | `Diagnostics.swift`, `ReportIssueView.swift` | Content-free event log to a private file; same rules. |
| Updates | `AppUpdater.swift` (Sparkle), `UPDATES.md`, `tools/package_update.sh` | Velopack fed from GitHub releases; see "Updates and releases". |
| Launch options, recovery | `LaunchOptions.swift`, `Controller+Recovery.swift` | Port; crash-left meeting chunks finished on next launch as on the Mac. |
| Local cleanup model | `LocalCleanup.swift`, `LocalCleanupFacts.swift`, `module-one/cleanup/Runner` | Same model through llama.cpp; see "Local cleanup model". |
| Apple Intelligence (meeting notes, modes, cleanup) | `AppleCleanup.swift` | Not ported. Meeting notes route list becomes None or Custom Provider. |

### Regex and text differences to watch

`RuleCleaner`, `CleanupGuard` and `Dictionary` lean on `NSRegularExpression`. .NET `Regex` differs:

- Offsets: both use UTF-16, so ranges map directly, but Swift `String` indexing in non-regex code counts grapheme clusters. Any `count`, `prefix(n)` or character loop in Swift needs `StringInfo`/`Rune` handling in C#, not `string.Length`.
- `\b`, `\w`, `\d` are Unicode-aware in both by default, but not identically; ICU `\w` includes some marks .NET leaves out. Use `RegexOptions.CultureInvariant` and test with the corpus's non-ASCII cases.
- Case-insensitive matching: ICU folds full case (`ß` vs `SS`), .NET does simple case mapping. Port case-insensitive comparisons with `StringComparison.OrdinalIgnoreCase` only after checking the Swift side does not rely on full folding.
- Template syntax differs: `$1` works in both, but ICU escapes with `\$`, .NET with `$$`.
- Use `RegexOptions.NonBacktracking` only where no lookaround or backreference is needed; otherwise set a match timeout, since dictated text is untrusted.

## Speech recognition

The Mac runs NVIDIA Parakeet Unified English 0.6B as the Core ML conversion `FluidInference/parakeet-unified-en-0.6b-coreml@d32e972` (batch, plus a streaming folder for live words) and, for dictionary support, `parakeet-ctc-110m` rescoring inside FluidAudio. Windows uses **the same checkpoint**, `nvidia/parakeet-unified-en-0.6b`, in ONNX form.

What exists (checked 2026-11-18 through the Hugging Face and GitHub APIs):

- sherpa-onnx's own model release (`k2-fsa/sherpa-onnx`, tag `asr-models`) ships `sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming.tar.bz2` and int8 streaming builds at 240, 560 and 1120 ms chunks (about 501 MB each).
- The same files are on Hugging Face under `csukuangfj2/` (the sherpa-onnx maintainer's account): `…-int8-non-streaming` at revision `8c3a10fb13408c7a7054f6898958bf1c64a8d6c7` (encoder 654 MB, decoder, joiner, `tokens.txt`), a full-precision `…-non-streaming` at `e69c9fb5322532e9995b116b6f1e4b34ab0c4b13` (2.4 GB of encoder weights), and `…-int8-streaming-560ms` at `7551fd26fc810cc1e4e043e608db4d13b59be31e`.
- The upstream model is under the NVIDIA Open Model License (Hugging Face tag `license:other`), as THIRD_PARTY_NOTICES already records for the Mac. The sherpa-onnx repositories declare no license of their own and carry NVIDIA's model card files. Decided: the NVIDIA license governs the conversion, as for the Mac's Core ML conversion.

Plan:

1. **Model:** sherpa-onnx's Parakeet Unified build, run as a NeMo transducer. Measure both the int8 and the full-precision non-streaming builds against the Mac on the same real recordings (`module-one/models/benchmark.py` corpus format), then pin the one closest to the Mac's WER at acceptable latency. A WER close to the Mac's is the bar, not identical output. TDT v2/v3 are not needed: the conversion exists. They stay as a contingency only if the unified build fails on Windows, and that would go back to the user first.
2. **Engine:** sherpa-onnx's .NET package (`org.k2fsa.sherpa.onnx`, current 2.0.1; runtime packages exist for win-x64, win-arm64 and osx-arm64) with ONNX Runtime on the CPU. GPU providers only if CPU latency misses the target **(verify which providers the Windows packages ship)**. Because osx-arm64 natives exist, the helper and its protocol can be developed and benchmarked on this Mac too.
3. **Pins:** add the chosen builds to `module-one/models/artifact-pins.json` with revision, sizes and SHA-256 per file, so `verify_artifacts.py` and the `ModelDownload` port check them unchanged. Download from the pinned Hugging Face revision, never bundled; show the NVIDIA license before download, as on the Mac.
4. **Live words** (`--stream`): the int8 streaming build (560 ms first; 240 ms if it feels slow), display only, never inserted, as on the Mac.
5. **Dictionary boosting:** sherpa-onnx's offline NeMo transducer recognizer (`sherpa-onnx/csrc/offline-recognizer-transducer-nemo-impl.h`, master, read 2026-11-18) accepts `modified_beam_search` with a hotwords file and `hotwords_score`, including TDT models. So hotwords are the first approach; how well they bias Parakeet Unified toward dictionary names is measured in the M3 spike. The output goes through the ported `BoostFilter`, so only non-ordinary words can change, as on the Mac. If hotwords don't work for this model, the next candidate is porting FluidAudio's CTC rescoring (Apache-2.0) over the ONNX `csukuangfj/sherpa-onnx-nemo-parakeet_tdt_ctc_110m-en-36000-int8` (exists on Hugging Face); the decision comes back to the user with measurements. Post-transcription dictionary respelling (`Dictionary.swift`) applies either way.
6. **Timed words** (`{"timed":…}`) for speaker splitting: sherpa-onnx returns token timestamps for transducer results **(verify for this model)**.
7. **Speaker separation** (`--diarize`): sherpa-onnx offline diarization (pyannote segmentation-3.0, MIT, plus a speaker-embedding model and clustering). This is not the Mac's community-1 + VBx pipeline; the app-side speaker checks still apply, and the threshold is tuned on real meetings.

## Local cleanup model

The Mac's "Local model" cleanup is **Vedu Scribe 0.8B**, `heyvedu/vedu-scribe-0.8b` at revision `7dced5841c58d7d6e0118c4c123f222b35057871`, Apache-2.0, a fine-tune of `Qwen/Qwen3.5-0.8B` (architecture `Qwen3_5ForConditionalGeneration`). The pinned files are an **MLX checkpoint with mixed 4/8-bit affine quantization** (group size 64), not full-precision weights. Windows runs the same weights.

What exists (checked 2026-11-18):

- No GGUF or ONNX from HeyVedu. The only GGUF is a third-party one, `sumitsah35/vedu-scribe-0.8b-gguf@4b72e79` (Q8_0, 812 MB, Apache-2.0, states it is modified). Its README describes the conversion: dequantize the pinned MLX checkpoint to bf16 with `mlx_lm convert --dequantize` (mlx-lm 0.32.2), undo mlx-lm's Qwen3.5 load-time changes (tensor names, `conv1d` axis order, RMSNorm stored as `1 + w`; script in the repo), then llama.cpp `convert_hf_to_gguf.py` at commit `c7fc509`. It claims output identical to MLX on 32 dictations. That claim is unverified.
- LLamaSharp (MIT; current v0.30.0, October 2026) added Qwen3.5 support in its release history **(verify the shipped llama.cpp build loads this architecture)**.

Plan:

Why llama.cpp over ONNX Runtime GenAI: there is a written, tested-by-someone conversion path from this exact MLX checkpoint to GGUF, and LLamaSharp lists Qwen3.5 support. ONNX Runtime GenAI's support for Qwen3.5's hybrid linear-attention layers was not evaluated; it stays an option only if llama.cpp fails.

"Same weights" has a limit: no full-precision Vedu Scribe checkpoint is published, only the MLX 4/8-bit one. Dequantizing it gives exactly the values the Mac computes with; an F16 GGUF keeps them; Q8_0 re-quantizes them, so its outputs can drift slightly from the Mac's.

1. **Convert ourselves, reproducibly.** A script in `module-one/cleanup/` runs the same three steps at pinned tool versions on this Mac and writes F16 and Q8_0 GGUF files. If our Q8_0 matches the third-party file's SHA-256, that confirms both conversions; either way we ship our own build, not a stranger's file.
2. **Host:** the GGUF is a modified Apache-2.0 work, so it can be redistributed with the license and a modification notice. Decided: a Hugging Face repository under the user's account, pinned by revision like every other model. The files, model card and pin are prepared here; the repository is created and uploaded only after the user says so.
3. **Runtime:** `Murmur.Cleanup` hosts LLamaSharp on the CPU backend first, with the Mac's exact settings: system message `Clean up this dictation.`, the `[tone: …]` and vocabulary header, at most 12 terms, thinking off (`enable_thinking: false`), temperature 0, the same input-token cap (400) and output cap, input limit 4,000 characters (`LocalCleanup.swift:27`). The local model stays on the strict guard (`Processing.swift:287`).
4. **Prompt and token parity:** build the ChatML string ourselves (system, user, assistant with an empty `<think></think>` block) instead of relying on llama.cpp's embedded chat template to honor `enable_thinking: false`. A test tokenizes the same inputs with the MLX tokenizer and the GGUF tokenizer and compares token IDs, since the 400-token `too-long` cutoff depends on the counts matching.
5. **Validation:** run `cleanup-corpus.json` through the Mac's MLX helper and the Windows GGUF helper with greedy decoding, compare outputs case by case, and report exact-match rate and guard outcomes; add a local-model section to `CLEANUP_BENCHMARK.md`. Bar: guard pass rate and corpus results within a small, stated margin of the Mac's; any difference is listed, not hidden. Pick Q8_0 if it meets the bar, otherwise F16 (about 1.6 GB).
6. **Pins and notices:** add the GGUF to `artifact-pins.json`; add LLamaSharp, llama.cpp and the model's modification notice to `THIRD_PARTY_NOTICES.md`.

## Website matching for modes

Required in v1, as on the Mac (`ScreenReading.swift:10-38`: the page URL from the browser's web area, read only for mode matching, never stored or logged).

- **Chromium browsers** (Chrome, Edge, Brave, Vivaldi, Opera, Arc): read the URL from the focused tab's document element through UI Automation (Chromium exposes the page URL as the document's value) and, failing that, the address bar edit control's `ValuePattern` **(verify both per browser)**. Chromium builds its accessibility tree only after a UI Automation client appears, the same priming problem the Mac handles in `Insertion.swift:82-104`; we ask once per process, at recording start.
- **Firefox:** the address bar (`urlbar-input`) through UI Automation **(verify)**.
- **Rules:** read only when focus is in page content, never while the user is typing in the address bar (that value is not a URL yet); parse to a host and match with the ported `WritingModes` site rules; drop the value after matching; skip private windows' address bars only if they behave differently (no special casing otherwise).
- **Test matrix** (manual on the user's PC, then a scripted UI Automation probe in CI where possible): Chrome, Edge, Firefox, Brave × {normal window, private window, second window, after tab switch, installed web app (PWA) window, F11 full screen with hidden toolbar, address bar focused, non-English browser UI, 100% and 150% display scaling, a page inside an iframe-heavy app such as Gmail}. Each case records the expected host and whether the mode matched.
- A browser we can't read gets app-level matching only, and Settings says so for that browser. This is a coverage limit shown to the user, not a silent fallback.

## Learning from edits

Required in v1. The Mac watches the field after a paste for up to 60 s, polls every 0.25 s, settles after 2 s of stable text, and falls back to following keystrokes when the field can't be read (`Observation.swift`, `Controller+Learning.swift`, `Learning.swift:39-70`).

1. **Primary: UI Automation read-back.** Keep the element captured at paste (runtime ID, process, window). Poll `ValuePattern`/`TextPattern` on that element only, with the Mac's timings. Stop on focus leaving the element, the next insertion, 60 s, or settle. Never read an `IsPassword` element. Only the inserted span and its edited form go to `CorrectionFilter`; nothing else in the field is kept.
2. **Fallback: scoped keystrokes.** The low-level keyboard hook already exists for hold-to-talk (Right Ctrl needs it). When the field can't be read, the hook's callback forwards key events to the learning observer only while all of these hold: within 60 s of this dictation, the foreground window and focused element are still the paste target, the element is not a password field, and the observer has not settled. It keeps an in-memory edit model of the inserted span (characters, Backspace, Delete, arrows, selection-replacing keys); anything else (a click, Ctrl shortcuts other than undo, focus change) ends the observation. Key-to-character translation uses `ToUnicodeEx` with the flag that leaves keyboard state untouched, so dead keys and IME input aren't disturbed; IME composition ends the observation.
3. **Never stored.** Keystrokes are never logged, written to disk or sent anywhere; only a settled correction that passes `CorrectionFilter` reaches the ledger, as on the Mac. Diagnostics record content-free counts only.
4. **Documented:** the README privacy section and Settings → Learning explain both modes in plain words, and the learning toggle turns off both.
5. **Antivirus and privacy risk:** a global keyboard hook combined with clipboard access and `SendInput` matches keylogger heuristics. Unsigned or new builds may be flagged by Microsoft Defender, SmartScreen or corporate EDR tools, and some workplaces block such apps. Mitigations: sign every binary, submit releases to Microsoft's malware analysis portal for false-positive review, keep the hook in the main signed executable, and say in the docs exactly what the hook does. This risk exists because of the hold-to-talk hook regardless of learning; learning only adds the in-memory interpretation.

## Meeting capture

- **Me:** the microphone through WASAPI, as dictation.
- **Them:** WASAPI process loopback (`ActivateAudioInterfaceAsync` with `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`). `PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE` targets one call app's process tree (the Mac's `TapTarget` for one app); `EXCLUDE_TARGET_PROCESS_TREE` with Murmur's own PID gives "everything except Murmur". This API needs Windows 10 build 20348 / Windows 11 for the documented behavior, and NAudio does not wrap it, so it needs our own COM interop **(verify minimum build; Microsoft's sample states 20348)**.
- **Clock alignment:** process loopback delivers no packets while the target is silent. Port `SystemAudioTap`'s silence fill so the Them stream stays aligned with Me, using the device clock and `AUDCLNT_BUFFERFLAGS_SILENT`.
- **Process tree limits:** a call in a browser tab means capturing the whole browser tree; new Teams plays audio from `msedgewebview2.exe` children of `ms-teams.exe`, which the tree mode covers **(verify on the user's PC)**. Re-resolve targets on refresh like `refreshProcesses()`.
- **Chunks, store, notes, export:** `MeetingStreamFeed`, `MeetingStore`, `MeetingNotes`, `MeetingExport` port to Core unchanged in behavior, including crash recovery of `<stream>-<start ms>.wav` chunks.
- **Notes routes:** None or Custom Provider (Apple Intelligence has no Windows equivalent). Corpus checks reuse `meeting-notes-corpus.json` and the held-out set.

## Insertion

- Save the current clipboard (all formats that can be read; delayed-render formats can't be saved and are lost, as with any clipboard tool), stage the text, post Ctrl+V with `SendInput`, restore later only if the clipboard still holds our item, as `Insertion.swift` does.
- **Privacy:** staged dictations must stay out of Windows clipboard history (Win+V) and cloud clipboard sync. Set the `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory = 0` and `CanUploadToCloudClipboard = 0` formats on our item **(verify behavior on Windows 11)**. This has no Mac counterpart and is a hard requirement.
- Refuse to paste into a focused password element (UI Automation `IsPassword`), matching the Mac's Secure Input refusal.
- **Limit:** a normal-integrity app cannot send input to an elevated window (UIPI). Paste into an admin terminal will fail; report it as a content-free failure rather than running Murmur elevated.
- Paste-last-dictation hotkey: same behavior as the Mac's ⌃⌘V path; default Ctrl+Win+Shift+V (decided).

## Keeping the two apps in step

Decided 2026-11-18: Mac and Windows stay separate native apps kept in step. Changes are built and tested on the Mac first, then ported. Three kinds of code, handled differently (details and the per-PR checklist: [CONTRIBUTING.md](CONTRIBUTING.md)):

- **Data, written once:** corpora, golden outputs, word lists, prompts, the settings key/default table, writing-mode presets. Both apps should load the same files.
- **Logic, ported:** C# files mirroring the Swift files one to one, checked against golden outputs.
- **UI and platform plumbing, rewritten:** from the specs in `spec/`, checked by hand on Windows.

### Shared data

On this branch the Mac code is not changed to read shared files. Instead the direction is reversed for now: small Swift generators (`windows/shared/export_*.swift`) compile against the Mac sources and export their constants to `windows/shared/*.json`, which `Murmur.Core` embeds and reads. The Swift constants stay the source of truth, and nothing is hand-copied into C#.

The corpora are read in place (`app/cleanup-corpus.json`, `app/meeting-notes-corpus.json`, `app/meeting-notes-heldout.json`).

**Later, as a separate Mac PR for the user to approve:** move these Swift constants into the shared JSON files so the Mac loads them too, then delete the export generators.

| Swift source | Constants |
| --- | --- |
| `CleanupText.swift` | functionWords, hesitations, negation sets and stems, truncatedNegations, cardinals, ordinals, abbreviations, titles |
| `CleanupGuard.swift` | assistantPhrases, answerPhrases, polishFillers, polishFillerPairs, grammarWords, droppableGrammarWords, modals, persons, correction and repair cue lists, noQuantified, ordinalPhraseNouns |
| `RuleCleaner.swift` | stutterWords, commaKeepers, cuePatterns, weekdays, months, relativeTimes, colors, list cues, cardinal/ordinal words, markCommands, atPhrases, command blockers, domainEndings, spoken and Indian scales, emailFrame, signOff |
| `Learning.swift` | negations, negatingPrefixes, numberWords, antonyms, contextualWords, inflectionSuffixes, and the similarity thresholds |
| `Dictionary.swift`, `VoiceCues.swift` | commandPhrases, length limits, leadWords, reserved, closing |
| `CleanupPrompt.swift`, `EditPrompt.swift`, `MeetingNotes.swift`, `module-one/cleanup/Runner/.../main.swift` | prompt texts and tails, edit limits, meeting-notes lexicons (stopwords, cues, units), the local cleanup system line and header |
| `Settings.swift`, `Routes.swift`, `Retention.swift`, others listed in [spec/settings.md](spec/settings.md) | settings keys and defaults (exported as `windows/shared/settings.json` when settings are ported in M2) |
| `WritingModes.swift` | presets (M4) |
| `CallApps.swift` | call-app catalog, with a Windows process-name column added (M5) |

Thresholds and timings (`AudioFormat.swift`, `Shortcut.swift`, `Observation.swift`) could move too, but they're single numbers. They stay as constants ported by hand unless the Mac PR decides otherwise.

### Parity contract

- **Golden files:** `windows/golden/golden_*.swift` run the Swift logic over the corpora plus synthetic edge cases (non-ASCII, emoji, long and punctuation-only text) and write `windows/golden/data/*.json`. The C# tests compare case by case. Golden files are generated on macOS only, since Linux Swift's Foundation can differ on exactly those inputs.
- **Lexicon in golden runs:** where the Mac uses `NSSpellChecker`, generators and C# tests inject the same fixed synthetic word list. The golden files then test the logic, not the two lexicons. The bundled Windows word list is tested separately.
- **Three checks, all failing on drift:**
  1. `windows/golden/generate.sh --check` (macOS) fails when a Mac change alters golden or shared output that wasn't regenerated.
  2. `dotnet test` (Linux and Windows) fails when the C# output disagrees with golden.
  3. `windows/tools/check_parity.py` fails when a ported Swift file changed since its port. It compares against the SHA-256 recorded in `windows/porting.json`.
- **Where they run:** the Mac's `app/check.sh` runs checks 1 and 3 as `windows_parity`, and CI (`.github/workflows/windows.yml`) runs all three.
- **Consequence:** a Mac PR that changes ported behavior fails CI until it is ported, or until the port is knowingly deferred.

## CI

- Workflow `.github/workflows/windows.yml`. It triggers on `windows/**`, `app/**` and the workflow itself: `app/**` was added to the approved `windows/**` scope because the parity contract must fail on Mac-side changes. `windows/porting.json` records which Swift file each port matches.
- Jobs: `core` on `ubuntu-latest` and `windows-latest` (C# tests; the parity hash check on Linux); `golden` on `macos-latest` (`generate.sh --check`). `app` on `windows-latest` (build the solution, run tests, publish an unsigned self-contained build as an artifact).
- Pin actions by commit SHA, as `secrets.yml` does. No secrets in pull-request jobs.
- Release workflow (M6): runs on a tag, builds, signs with Azure Artifact Signing (or signtool with a hardware-backed cert on the user's VM), packs with Velopack, uploads to a draft GitHub release. Publishing stays manual.

## Updates and releases

**Choice: Velopack** (MIT). It installs per user without admin rights, updates from GitHub Releases through its `GithubSource`, builds delta packages, and signs during packing, including Azure Artifact Signing (formerly Trusted Signing) directly from version 1.3. MSIX was considered: it needs signing for sideloading too, virtualizes some file and registry access, and offers nothing here that we need (no API requires package identity). The Microsoft Store is out of scope for v1.

**Install and data folders.** Velopack installs to `%LOCALAPPDATA%\<packId>\` with the app in `current\` beside `Update.exe`; an update replaces `current\`, and uninstall removes the whole `<packId>` folder (Velopack docs: "During install/uninstall, the entire `{packId}` folder is replaced or removed"). So:

- Pack id `MurmurApp`; app at `%LOCALAPPDATA%\MurmurApp\current\`.
- All user data in `%LOCALAPPDATA%\Murmur\` (settings, History, dictionary, ledger, models, retry audio). Updates never touch it.
- Uninstall leaves user data, as on the Mac; Delete All Data in Settings removes it. Nothing is written inside `current\`.
- Credentials live in Credential Manager and survive updates and uninstall; Delete All removes them.

**Feed and assets.** `vpk pack` produces, per channel (default `win` for x64; `win-arm64` later): `MurmurApp-win-Setup.exe`, `MurmurApp-win-Portable.zip`, `MurmurApp-<version>-full.nupkg`, `MurmurApp-<version>-delta.nupkg`, `releases.win.json` (the feed the client reads) and `assets.win.json` (used by `vpk` itself) **(verify exact names with the pinned vpk)**. The feed has hashes for each package; packages and the executables inside are Authenticode-signed.

**Living next to the Mac release.** The Mac's Sparkle feed is `https://github.com/theaakashb/Murmur/releases/latest/download/appcast.xml` (`app/UPDATES.md`, `app/tools/package_update.sh:13-15`), so **whatever release GitHub marks "latest" must contain `appcast.xml`**. Two release patterns:

- **Joint release** (Mac and Windows ship the same version): one tag `vX.Y.Z` with `Murmur-<build>.zip`, `appcast.xml` and the Windows assets, uploaded with `vpk upload github --tag vX.Y.Z --merge`. Marked latest as today.
- **Windows-only release:** tag `windows-vX.Y.Z`, published with "Set as latest" unchecked (or `gh release edit --latest=false`), so the Mac feed keeps pointing at the last Mac release.

Why this works, from Velopack's source (`velopack/velopack` branch `develop`, read 2026-11-18): the client's `GithubSource.GetReleases` lists `/releases?per_page=10` ordered by publish date, skipping prereleases unless opted in, and `GitBase.GetReleaseFeed` collects the assets of **every** listed release that has `releases.{channel}.json`. It never reads `/releases/latest`, so "latest" doesn't matter to Windows. Limit: only the 10 most recently published releases are searched; a Windows release followed by more than 10 Mac-only releases is no longer seen, which only matters if a Windows client still needs it. `vpk upload github` creates a draft and publishes without setting `make_latest` (`src/vpk/Velopack.Deployment/GitHub.cs`), so the latest choice is made by hand when publishing. Older Mac builds with the pre-Sparkle GitHub checker (introduced in `5f5c0f8`, present in `v0.2.0`) list `/releases?per_page=30` and skip tags that don't parse as SemVer with an optional `v`, so `windows-v…` tags are not offered to them as Mac updates (read from that commit's code, not run).

**Release flow** (M6, `.github/workflows/windows-release.yml`, run manually on a tag):

1. Build and test on `windows-latest`; `dotnet publish` self-contained, x64.
2. `vpk download github` fetches the previous Windows release so a delta can be built; it lists all releases (`GetAll`), not just latest.
3. `vpk pack --packId MurmurApp --packVersion X.Y.Z …` with `--azureTrustedSignFile` (Azure Artifact Signing) or `--signParams` (signtool with a certificate on the user's VM). Every timestamped.
4. `vpk upload github --tag … --merge` to a **draft** release. Publishing (and the "latest" choice above) stays a manual step by the user, as on the Mac.
5. `appcast.xml` and the Mac ZIP are still produced by the Mac flow; the Windows flow never rewrites them.

**Client behavior.** Check at launch and every few hours when `updateChecksEnabled` is on; download in the background; apply on the next restart or when the user clicks Restart to update. Update checks send nothing but the HTTP request for the public feed: no transcripts, audio, dictionary entries or keys, as on the Mac. Prereleases are off unless the user opts in.

**Rollback.** Velopack does not roll back on its own. A bad release is fixed forward: publish a higher version built from the last good commit. The client also supports an explicit downgrade (`AllowVersionDowngrade`) for a support case, never automatically. Each release keeps its full package, so any version can be reinstalled from its Setup.exe. User data is outside the install folder, so neither path touches it; any future change to the data format must stay readable by the previous release or ship with that release's fix-forward plan.

**Integrity.** Velopack verifies package hashes from the feed; it does not check the publisher of the next version's signature by default **(verify)**. Trust rests on HTTPS and the GitHub account, as it would without Sparkle's EdDSA key. Hardening (refuse an update whose `MurmurApp.exe` signer differs from the running one) is an M6 task if Velopack exposes a hook for it.

**Signing.** Azure Artifact Signing (cheaper, no hardware token; needs an identity-validated Azure account, with eligibility limits for individuals **(verify for the user)**) or an Authenticode certificate. SmartScreen reputation builds with downloads either way.

**Start at login:** a `HKCU\…\Run` entry pointing at Velopack's stable launcher, toggled in Settings.

## Minimum OS and architecture

- Decided: **Windows 11 (22H2 or later), x64 first**. Windows 10 reached end of support on October 14, 2025, and process loopback's documented minimum is a server/Windows 11-era build. ARM64 (Snapdragon PCs) as a second target once x64 works, on its own update channel `win-arm64`: .NET and WinUI 3 ship ARM64 builds, and a `org.k2fsa.sherpa.onnx.runtime.win-arm64` NuGet package exists **(verify LLamaSharp's ARM64 backend)**.

## Milestones

Each milestone ends with a local commit on a branch, CI green, and a short report. Nothing is pushed or merged without user approval.

| # | Milestone | Exit criteria |
| --- | --- | --- |
| M1 | Core text engine | Solution, CI and parity tooling. `Murmur.Core` ports CleanupTypes, CleanupText, CleanupGuard, RuleCleaner, Cleanup, CleanupPrompt, Dictionary, Learning, VoiceCues, PrivateFiles (behavior) and BoostFilter; their data constants come from `windows/shared/`. Exit: `dotnet test` passes with every golden case matching the Mac's output, including all 426 cleanup-corpus cases through `RuleCleaner` and `CleanupPipeline`; ported `check_*` assertions pass; `generate.sh --check` and `check_parity.py` pass. Runs entirely from this Mac. `Processing` moves to M2: its check needs the provider and route files ported there. |
| M2 | Dictation shell, cloud route | WinUI tray app, bubble, hold-to-talk hook, WASAPI recording, Murmur Cloud and Custom Provider transcription and cleanup, paste insertion with clipboard-history exclusion, credentials, settings file, History with retention. Exit: on the user's PC, 20 real dictations into Notepad, a browser, Teams/Slack and VS Code with no lost clipboard and no transcript in Win+V history. |
| M3 | Local ASR and local cleanup | `Murmur.Speech` with Parakeet Unified ONNX (pinned, verified download), warm runner, chunking, live words, hotword spike result. `Murmur.Cleanup` with our Vedu Scribe GGUF. Exit: WER and latency on the user's PC for 5/30/120 s real dictations next to the Mac's on the same recordings; cleanup corpus comparison against the Mac's MLX output written up in `CLEANUP_BENCHMARK.md`. The conversion and corpus comparison can start on this Mac before M2. |
| M4 | Modes, voice edit, learning | Writing modes with app and website matching, voice edit, learning (UI Automation read-back plus scoped keystrokes), voice cues, tips, welcome guide. Exit: ported checks pass; the browser test matrix filled in; learning verified on readable and unreadable fields on the user's PC. |
| M5 | Meetings | Process-loopback capture with silence fill, call detection and offer, meeting store, notes via Custom Provider, export, speaker separation. Exit: a real 30-minute Teams or Zoom call on the user's PC with aligned Me/Them, crash recovery tested by killing the process, notes corpus checks pass. |
| M6 | Release | Velopack packaging, signing, release workflow, both release patterns tried on a test repository first, Windows sections in README, CONTRIBUTING, SECURITY, THIRD_PARTY_NOTICES. Exit: on the user's PC and a clean VM: signed install, a delta update from one signed build to the next with History and settings intact, a fix-forward update, uninstall leaving user data; the Mac appcast still resolves after a Windows-only release. |

## What can be done from this Mac

- Today: no .NET SDK is installed on this Mac (`dotnet` not found). Installing it (Homebrew offers `dotnet-sdk`, currently 11.0.100; .NET 10 LTS would also be needed or targeted) needs the user's approval.
- With the SDK: all of `Murmur.Core` and its tests, the Swift differential tool, the Linux CI job, and `Murmur.Speech` logic up to the sherpa-onnx calls (an `org.k2fsa.sherpa.onnx.runtime.osx-arm64` package exists, so the helper and its protocol can run here with the same ONNX model), and the cleanup GGUF conversion plus the Mac-side corpus comparison (this Mac already runs the MLX model). LLamaSharp has macOS backends too, so `Murmur.Cleanup` can be compared against MLX on the same machine **(verify)**.
- Not from this Mac: building WinUI 3 (`Murmur.App`), anything touching WASAPI, hooks, SendInput, UI Automation, Credential Manager, process loopback, signing, installer tests. Those need Windows CI for builds and the user's PC for real behavior.

## What is needed from the user

1. Done: .NET SDK 10.0.404 installed to `~/.dotnet` and a Python virtual environment at `~/.venvs/murmur-convert` (packages added when the conversion runs).
2. Approved: `.github/workflows/windows.yml`, scoped to `windows/**`.
3. For M2 onward: a way to run builds on the Windows PC, such as a Paseo host or SSH on the PC, with a GitHub checkout, the .NET SDK and the Windows App SDK prerequisites. Real dictation and meeting tests are done by the user on that PC.
4. For M6: the Azure Trusted Signing account, and a VM to hold signing access if signing should not run on GitHub-hosted runners.

## Open questions

All Phase 1 questions are answered (see "Decisions already made").

## Risks

- **Behavior drift between Swift and C#.** Two implementations of the same rules will diverge. Mitigated by shared corpora, ported checks and the differential tool, but only for text logic; timing and platform behavior can only be compared by hand.
- **Local ASR quality and dictionary support.** A different model and no CTC rescoring may make Windows transcripts measurably worse than the Mac's, especially for dictionary names. Measured in M3 before building on it.
- **Antivirus flags.** A global keyboard hook with clipboard access and synthetic paste looks like a keylogger to heuristics; see "Learning from edits". Signing and false-positive submissions reduce, but don't remove, the risk.
- **Release coupling with the Mac feed.** A Windows release marked "latest" without `appcast.xml` would break Mac updates. The release flow keeps Windows-only releases off "latest"; M6 tests this on a test repository first.
- **Model conversions.** Both models are third-party conversions or our own; quality is close to the Mac's, not identical, and is measured, not assumed.
- **Process loopback.** Less-used API with few examples; per-app capture of browser and WebView2-based calls needs real testing. Mitigation: M5 starts with a capture probe like `module-one/capture`.
- **Paste reliability.** Clipboard restore races with apps that read the clipboard late, clipboard managers, and elevated windows. Measured in M2 across real apps.
- **WinUI 3 maturity.** Mitigated by the M2 shell spike with a WPF fallback decision before most UI exists.
- **One maintainer, two apps.** Every Mac feature change now has a Windows cost. The porting-status table makes that visible.
