# Murmur for Windows: plan

Status: Phase 1 (planning). No Windows code exists yet. This plan was written on a Mac; nothing in it has been built or run on Windows. Claims about Windows APIs and third-party libraries are marked **(verify)** where they have not been checked against the installed version.

## Decisions already made

- Native C#/.NET app in this repo under `windows/`. The Mac app stays Swift.
- V1 is a full port: hold-to-talk dictation, cleanup and its routes, Murmur Cloud, dictionary and learning, writing modes, voice edit, History and retention, settings, local speech recognition at launch, and meeting capture (microphone plus system audio, call detection, speaker separation).
- Insertion is paste only: clipboard plus Ctrl+V, with a paste-last-dictation hotkey. No typing fallback and no "couldn't confirm" badge (matches the Mac after PR #94).
- Signing: Azure Trusted Signing or an Authenticode certificate, bought by the user. Real testing on the user's Windows PC; a Windows VM can serve CI and signing.
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
  Murmur.Windows.sln
  Directory.Build.props        shared settings: net10.0, nullable, warnings as errors
  src/Murmur.Core/             net10.0, no Windows APIs: everything Foundation-only on the Mac
  src/Murmur.App/              net10.0-windows: WinUI 3 shell plus the Windows platform layer
  src/Murmur.Speech/           net10.0 console helper: local ASR and diarization, the ModelBench line protocol
  tests/Murmur.Core.Tests/     xUnit, runs on Windows, Linux and macOS
  spec/                        written specs derived from the Swift code
```

Four projects, no more until a concrete need appears. Platform code (audio, hotkeys, clipboard, UI Automation, credentials) lives in `Murmur.App` under a `Platform/` folder rather than its own project; it can only be tested on Windows anyway.

**Murmur.Core** ports the roughly 60 Foundation-only Swift files that `app/check.sh` already builds in isolation: `CleanupTypes`, `CleanupText`, `CleanupGuard`, `CleanupPrompt`, `Cleanup`, `RuleCleaner`, `Dictionary`, `Learning`, `Processing`, `Retention`, `History`, `PrivateFiles` (behavior only), `Routes`, `ProviderTypes`, `ProviderHTTP`, `OpenAICompatible`, `MurmurCloud`, `MurmurCloudEdit`, `EditPrompt`, `WritingModes`, `VoiceCues`, `Meetings`, `MeetingStore`, `MeetingExport`, `MeetingNotes`, `MeetingSpeakers`, `CallApps`, `CallDetection`, `ModelManifest`, `ModelDownload`, `Digests`, `WordDiff`, `Insights`, `Tips`, `BoostFilter`. It builds and tests on this Mac, Linux CI and Windows. The Windows-specific lexicon and clock come in through interfaces, as `isCommonWord` does in Swift.

**Murmur.App** is the tray app: tray icon and menu, the recording bubble, Settings, History, Meetings, the welcome guide, and the platform layer.

**Murmur.Speech** is a separate process for the same reasons ModelBench is one on the Mac: a native-library crash or a stuck model cannot take down the app, memory goes back to the OS when it exits, and the app side (warm runner, chunk jobs, timeouts, one-shot fallback) can be ported one to one from `app/ModelHelper.swift`. It speaks the same line protocol as `module-one/models/Runner/Sources/ModelBench/ModelBench.swift`: `--serve` (one JSON line per request: an audio path, `{"samples":BASE64}`, `{"timed":PATH}`, `{"boundary":PATH}`), `--stream RATE` (`{"partial":…}`), `--diarize KEPT_WAV THRESHOLD` (`{"segments":[…]}`), with `{"ready":true}` first and `<unk>` scrubbed from every line.

The local cleanup model (Vedu Scribe through MLX on the Mac) would be a second helper if it is ported; see open question 1.

### UI framework: WinUI 3

Recommendation: **WinUI 3** (Windows App SDK), unpackaged, self-contained.

- It gives the Windows 11 look (Fluent controls, Mica, system accent, light/dark) that the Mac app's design standard (`app/DESIGN.md`) implies. WPF would need a third-party theme to look current.
- The parts that are hard in either framework are the same Win32 work: a topmost, no-activate, click-through bubble window (`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT`), the tray icon, the low-level keyboard hook. So WPF's maturity buys little here.
- Cost: WinUI 3 cannot be built on macOS or Linux; its XAML compiler runs only on Windows **(verify)**. WPF can be compiled elsewhere with `EnableWindowsTargeting`. Since all logic lives in `Murmur.Core`, which builds anywhere, the shell being Windows-only to build is acceptable; Windows CI catches shell build breaks.
- Risk: WinUI 3 has a history of rough edges (tray support needs a helper such as H.NotifyIcon, window-sizing APIs are thinner than WPF). If milestone M2's shell spike hits a blocker, switch to WPF at that point, before much UI exists.

### Runtime and versions

- .NET 10 (LTS, released November 2025, supported to November 2028) rather than .NET 11 (STS) **(verify support dates)**.
- Windows App SDK 1.8 or later, self-contained so users need no runtime install **(verify current version)**.
- Libraries, each to be license-checked and added to `THIRD_PARTY_NOTICES.md` before use: NAudio (MIT) for WASAPI capture, sherpa-onnx (Apache-2.0) with ONNX Runtime (MIT) for ASR and diarization, Velopack (MIT) for install and updates, H.NotifyIcon (MIT) for the tray. No other dependencies without a reason.

## Subsystem map

| Subsystem | Mac source of truth | Windows approach |
| --- | --- | --- |
| Hold-to-talk shortcut | `Shortcut.swift` (`ShortcutMachine`: hold, double-press toggle, short-tap cancel), event tap thread | Same state machine in Core. Input from `WH_KEYBOARD_LL` hook on its own thread (needed for modifier-only keys and key-up). Presets differ: Right Alt is AltGr on many layouts, so default to Right Ctrl or Ctrl+Win (open question 4). |
| Recording | `Capture.swift`, `Session.swift`, `AudioFormat.swift` | WASAPI shared-mode capture via NAudio, converted to 16 kHz mono Float32. Same min-length, silence and chunking rules (`Chunker`). |
| Local ASR | `ModelHelper.swift`, `ModelBench.swift`, `ModelFiles/ModelFlow/ModelManifest/ModelDownload.swift`, `artifact-pins.json` | `Murmur.Speech` on sherpa-onnx. See "Speech recognition". |
| Cloud transcription | `Routes.swift`, `CloudAudio.swift`, `OpenAICompatible.swift`, `MurmurCloud.swift`, `ProviderHTTP.swift` | `HttpClient` port in Core. Same endpoints, payloads, validation, redirect, size and timeout rules. No dictionary prompt. |
| Dictionary correction and lexicon | `Dictionary.swift`, `Processing.swift`, `BoostFilter.swift`; lexicon = `NSSpellChecker` (`Controller+Dictionary.swift:92`) | Core port. Lexicon on Windows: open question 3. |
| Cleanup | `Cleanup.swift`, `RuleCleaner.swift`, `CleanupPrompt.swift`, `CleanupGuard.swift`, `CleanupText.swift` | Core port, validated by `cleanup-corpus.json` and `check_cleanup`/`check_processing`. Regex rewrite notes below. |
| Writing modes and AI engine | `WritingModes.swift`, `AIEngine.swift`, `ScreenReading.swift` (browser URL, visible text) | Core port. App and site matching through UI Automation: process name for apps; browser URL from the address bar element **(verify per browser)**. Apple Intelligence engine has no equivalent: Custom Provider or Murmur Cloud only. |
| Voice edit | `VoiceEdit.swift`, `EditPrompt.swift`, `EditTrigger.swift`, `EditShortcut.swift`, `MurmurCloudEdit.swift` | Core port. Selected text via UI Automation `TextPattern`; never from a password field. |
| Insertion | `Insertion.swift`, `Delivery.swift` | Clipboard stage, `SendInput` Ctrl+V, delayed restore of the previous clipboard only if it still holds our item. See "Insertion" below. |
| Learning from corrections | `Learning.swift`, `Controller+Learning.swift`, field read-back in `Insertion.swift` | Core port. Read-back through UI Automation `ValuePattern`/`TextPattern`; skip `IsPassword` elements. Read-back is for learning only, never a retry. |
| History and retention | `History.swift`, `Retention.swift`, `PrivateFiles.swift`, `HistoryRouter.swift` | Core port. Files under `%LOCALAPPDATA%\Murmur` (not Roaming, so transcripts never sync), owner-only protected DACL, atomic replace. |
| Credentials | `Credentials.swift` (Keychain) | Windows Credential Manager generic credentials (`CredWriteW`, DPAPI per user). Same destination binding of keys. |
| Settings | `Settings.swift`, `Controller+Settings.swift`, many per-feature keys | One JSON settings file in the private folder, same key names and defaults as `spec/settings.md`. |
| Meetings | `Meetings.swift`, `MeetingCapture.swift`, `MeetingRecorder.swift`, `MeetingStore.swift`, `MeetingNotes.swift`, `MeetingExport.swift`, `MeetingSpeakers.swift`, `MeetingsUI.swift` | See "Meeting capture". |
| Call detection | `CallDetection.swift`, `CallApps.swift`, `CallOfferUI.swift`, `AudioProcessList` in `MeetingCapture.swift` | Same detector in Core; input from WASAPI audio session enumeration (`IAudioSessionManager2`, per-process capture/render activity) and a Windows call-app table (Teams, Zoom, Slack, Discord, Webex, browsers by process name). |
| Voice cues | `VoiceCues.swift` | Core port; sounds through WASAPI/`MediaPlayer`. |
| Tips, insights, welcome guide | `Tips.swift`, `Insights.swift`, `WelcomeGuide*.swift`, `ProfessionalHint*.swift` | Core logic port; WinUI views. Guide steps rewritten for Windows permissions (there is no Accessibility permission; microphone privacy toggle instead). |
| Diagnostics, report issue | `Diagnostics.swift`, `ReportIssueView.swift` | Content-free event log to a private file; same rules. |
| Updates | `AppUpdater.swift` (Sparkle), `UPDATES.md` | Velopack with GitHub releases. Update checks send nothing but the version. |
| Launch options, recovery | `LaunchOptions.swift`, `Controller+Recovery.swift` | Port; crash-left meeting chunks finished on next launch as on the Mac. |
| Local cleanup model | `LocalCleanup.swift`, `LocalCleanupFacts.swift`, MLX runner | Open question 1. |
| Apple Intelligence (meeting notes, modes, cleanup) | `AppleCleanup.swift` | Not ported. Meeting notes route list becomes None or Custom Provider. |

### Regex and text differences to watch

`RuleCleaner`, `CleanupGuard` and `Dictionary` lean on `NSRegularExpression`. .NET `Regex` differs:

- Offsets: both use UTF-16, so ranges map directly, but Swift `String` indexing in non-regex code counts grapheme clusters. Any `count`, `prefix(n)` or character loop in Swift needs `StringInfo`/`Rune` handling in C#, not `string.Length`.
- `\b`, `\w`, `\d` are Unicode-aware in both by default, but not identically; ICU `\w` includes some marks .NET leaves out. Use `RegexOptions.CultureInvariant` and test with the corpus's non-ASCII cases.
- Case-insensitive matching: ICU folds full case (`ß` vs `SS`), .NET does simple case mapping. Port case-insensitive comparisons with `StringComparison.OrdinalIgnoreCase` only after checking the Swift side does not rely on full folding.
- Template syntax differs: `$1` works in both, but ICU escapes with `\$`, .NET with `$$`.
- Use `RegexOptions.NonBacktracking` only where no lookaround or backreference is needed; otherwise set a match timeout, since dictated text is untrusted.

## Speech recognition

The Mac runs `FluidInference/parakeet-unified-en-0.6b-coreml` (Core ML) and, for dictionary support, `parakeet-ctc-110m` CTC rescoring through FluidAudio. Neither runs on Windows.

Plan:

1. **Engine:** sherpa-onnx with ONNX Runtime on the CPU first. DirectML or other GPU providers only if CPU latency on the user's PC misses the target **(verify which providers sherpa-onnx's Windows builds ship)**.
2. **Model:** NVIDIA Parakeet TDT 0.6B v2 in sherpa-onnx's ONNX conversion (int8), English. Upstream license CC-BY-4.0 **(verify at the pinned revision)**. The Mac's "unified" model is under the NVIDIA Open Model License and has no known ONNX conversion **(verify)**; Parakeet v3 (multilingual) is the other candidate. Pick by measured WER and latency on real dictations, recorded like `MODEL_BENCHMARK.md`.
3. **Pins:** a Windows section in `module-one/models/artifact-pins.json` (or a sibling `artifact-pins-windows.json`) with immutable revisions, sizes and SHA-256 for every file; the existing `verify_artifacts.py` and `ModelDownload` checks apply unchanged. Weights are downloaded, never bundled, and the license is shown before download, as on the Mac.
4. **Live words** (`--stream`): the Mac uses the unified model's streaming mode. On Windows, start with re-decoding the growing buffer with the offline model every ~0.5 s (display only, never inserted); switch to a streaming model only if that is too slow.
5. **Dictionary boosting:** the Mac rescoring has no Windows equivalent. Candidate: sherpa-onnx hotwords with modified beam search for transducer models **(verify support for NeMo TDT models)**, then the same `BoostFilter` (ported to Core) so only non-ordinary words get replaced. If hotwords do not work for this model, dictionary support on Windows is post-transcription correction only (`Dictionary.swift` respelling), which still exists. Spike in M3.
6. **Timed words** (`{"timed":…}`) for speaker splitting: sherpa-onnx returns token timestamps for transducer models **(verify for TDT)**.
7. **Speaker separation** (`--diarize`): sherpa-onnx offline diarization (pyannote segmentation-3.0, MIT, plus a speaker-embedding model and clustering). This is not the Mac's community-1 + VBx pipeline, so results will differ; `check_speakers`-style tests on the app side still apply, but the clustering threshold needs its own tuning on real meetings.

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
- Paste-last-dictation hotkey: same behavior as the Mac's ⌃⌘V path; default key is open question 4.

## Shared-spec strategy

1. **Corpora in place:** `Murmur.Core.Tests` reads `../../../app/*.json` through `<None Include=… CopyToOutputDirectory>` links, so the Mac and Windows always test against the same file. Changing a corpus case changes both platforms' expectations in one commit.
2. **Ported checks:** each `check_<module>.swift` becomes `<Module>Tests.cs` with the same cases in the same order and a header comment naming the Swift file and the commit it was ported from.
3. **Differential check (M1):** a small Foundation-only Swift tool, built by `app/check.sh` on Linux CI, runs `RuleCleaner`, `CleanupGuard` and `Dictionary` over a list of inputs (corpus plus generated edge cases: non-ASCII, emoji, very long, punctuation-only) and writes JSON. The C# test compares its own output case by case. Synthetic text only.
4. **Drift guard:** CI on any change to the ported Swift files or the corpora runs both the Swift checks (Linux) and the C# tests, so a Mac behavior change without a matching Windows change fails visibly. The port-tracking table lives in `spec/porting-status.md` (created in M1).

## CI

- New workflow `.github/workflows/windows.yml`, path filters: `windows/**`, `app/*.json`, the ported Swift files, and the workflow itself.
- Jobs: `core` on `ubuntu-latest` (build `Murmur.Core`, run tests, run the Swift differential tool); `app` on `windows-latest` (build the solution, run tests, publish an unsigned self-contained build as an artifact).
- Pin actions by commit SHA, as `secrets.yml` does. No secrets in pull-request jobs.
- Release workflow (M6): runs on a tag, builds, signs with Azure Trusted Signing (or signtool with a hardware-backed cert on the user's VM), packs with Velopack, uploads to a draft GitHub release. Publishing stays manual.

## Packaging, signing, updates

- **Velopack**: per-user install to `%LOCALAPPDATA%`, no admin prompt, delta updates from GitHub releases, signed setup and binaries. MSIX was considered: it gives clean install/uninstall and is needed for some APIs (package identity), but nothing here needs identity, MSIX adds Store-style restrictions on file and registry virtualization, and sideloading still needs signing. Velopack is the simpler path **(verify Velopack works with unpackaged WinUI 3 self-contained)**.
- **Signing:** Azure Trusted Signing is cheaper and avoids handling a hardware token; it requires an identity-validated Azure account (individual developers were eligible in some regions only; **verify eligibility for the user**). Without EV reputation, SmartScreen warnings fade only as downloads build reputation either way.
- **Start at login:** a `HKCU\…\Run` entry, toggled in Settings.

## Minimum OS and architecture

- Proposal: **Windows 11 (22H2 or later), x64 first**. Windows 10 reached end of support on October 14, 2025, and process loopback's documented minimum is a server/Windows 11-era build. ARM64 (Snapdragon PCs) as a second target once x64 works: .NET, WinUI 3 and sherpa-onnx all ship ARM64 builds **(verify sherpa-onnx ARM64 Windows packages)**. Open question 5.

## Milestones

Each milestone ends with a local commit on a branch, CI green, and a short report. Nothing is pushed or merged without user approval.

| # | Milestone | Exit criteria |
| --- | --- | --- |
| M1 | Core text engine | Solution and CI skeleton. `Murmur.Core` ports CleanupText, RuleCleaner, CleanupGuard, Dictionary, Learning, Processing (text parts), BoostFilter. Tests pass on Linux and Windows CI, including all 426 cleanup-corpus cases and the ported checks; the Swift differential check matches on every generated case. Runs entirely from this Mac. |
| M2 | Dictation shell, cloud route | WinUI tray app, bubble, hold-to-talk hook, WASAPI recording, Murmur Cloud and Custom Provider transcription and cleanup, paste insertion with clipboard-history exclusion, credentials, settings file, History with retention. Exit: on the user's PC, 20 real dictations into Notepad, a browser, Teams/Slack and VS Code with no lost clipboard and no transcript in Win+V history. |
| M3 | Local ASR | `Murmur.Speech` with the chosen Parakeet ONNX model, pinned and verified download, warm runner, chunking, live words, dictionary boost spike result. Exit: measured latency and WER on the user's PC for 5/30/120 s real dictations, recorded with machine details. |
| M4 | Modes, voice edit, learning | Writing modes with app/site matching, voice edit, correction learning through UI Automation, voice cues, tips, welcome guide. Exit: ported checks pass; manual run of each on the user's PC. |
| M5 | Meetings | Process-loopback capture with silence fill, call detection and offer, meeting store, notes via Custom Provider, export, speaker separation. Exit: a real 30-minute Teams or Zoom call on the user's PC with aligned Me/Them, crash recovery tested by killing the process, notes corpus checks pass. |
| M6 | Release | Velopack packaging, signing, update feed, release workflow, Windows sections in README, CONTRIBUTING, SECURITY, THIRD_PARTY_NOTICES. Exit: signed installer installs, updates from one signed build to the next, and uninstalls cleanly on the user's PC and on a clean VM. |

## What can be done from this Mac

- Today: no .NET SDK is installed on this Mac (`dotnet` not found). Installing it (Homebrew offers `dotnet-sdk`, currently 11.0.100; .NET 10 LTS would also be needed or targeted) needs the user's approval.
- With the SDK: all of `Murmur.Core` and its tests, the Swift differential tool, the Linux CI job, and `Murmur.Speech` logic up to the sherpa-onnx calls (sherpa-onnx has macOS builds, so the helper protocol can even be exercised here with the same ONNX model) **(verify the NuGet package ships osx-arm64 natives)**.
- Not from this Mac: building WinUI 3 (`Murmur.App`), anything touching WASAPI, hooks, SendInput, UI Automation, Credential Manager, process loopback, signing, installer tests. Those need Windows CI for builds and the user's PC for real behavior.

## What is needed from the user

1. Approval to install the .NET SDK on this Mac (for M1).
2. Approval to add `.github/workflows/windows.yml` (it runs on GitHub-hosted Windows runners; public repos run them free).
3. For M2 onward: a way to run builds on the Windows PC, such as a Paseo host or SSH on the PC, with a GitHub checkout, the .NET SDK and the Windows App SDK prerequisites. Real dictation and meeting tests are done by the user on that PC.
4. For M6: the signing choice (Azure Trusted Signing account or a certificate) and a VM to hold signing access, if signing should not run on GitHub-hosted runners.
5. Answers to the open questions below.

## Open questions

1. **Local cleanup model.** The Mac's "Local model" cleanup runs Vedu Scribe 0.8B on MLX. Options: (a) leave it out of Windows v1; Faithful and Strict still run locally, and Custom Provider already supports a local server such as Ollama or LM Studio on loopback; (b) port it through llama.cpp or ONNX Runtime GenAI, which needs a converted, pinned model and its own measured guard results. Recommendation: (a), since it removes a native LLM runtime from v1 and the loopback route exists.
2. **ASR model.** Accept Parakeet TDT 0.6B v2 (English) or v3 (multilingual) instead of the Mac's unified model, chosen by measurement on the user's PC?
3. **Lexicon.** The Mac uses `NSSpellChecker` ("is this an ordinary English word"), which guards learning and respelling. Windows options: the Windows Spell Checking API (`ISpellCheckerFactory`), which depends on the installed English language pack and differs from the Mac's; or a bundled English word list (e.g. SCOWL, permissive license) that behaves the same on every PC and in CI. Recommendation: bundled word list, for deterministic tests; it will still differ from the Mac's lexicon at the edges.
4. **Default shortcuts.** Right Alt can't be the default (AltGr on many keyboard layouts). Proposal: Right Ctrl hold-to-talk, Ctrl+Win+V for paste-last. Also, Fn is not visible to Windows on most keyboards.
5. **Minimum OS and architecture.** Windows 11 22H2+ x64 for v1, ARM64 later?
6. **Browser site matching for modes.** Reading the URL from browser address bars through UI Automation works on Chrome and Edge in practice but is undocumented and can break with browser updates **(verify)**. Accept app-only matching in v1 if it proves unreliable?
7. **Distribution.** GitHub releases only for v1 (no Microsoft Store, no winget until later)?

## Risks

- **Behavior drift between Swift and C#.** Two implementations of the same rules will diverge. Mitigated by shared corpora, ported checks and the differential tool, but only for text logic; timing and platform behavior can only be compared by hand.
- **Local ASR quality and dictionary support.** A different model and no CTC rescoring may make Windows transcripts measurably worse than the Mac's, especially for dictionary names. Measured in M3 before building on it.
- **Process loopback.** Less-used API with few examples; per-app capture of browser and WebView2-based calls needs real testing. Mitigation: M5 starts with a capture probe like `module-one/capture`.
- **Paste reliability.** Clipboard restore races with apps that read the clipboard late, clipboard managers, and elevated windows. Measured in M2 across real apps.
- **WinUI 3 maturity.** Mitigated by the M2 shell spike with a WPF fallback decision before most UI exists.
- **One maintainer, two apps.** Every Mac feature change now has a Windows cost. The porting-status table makes that visible.
