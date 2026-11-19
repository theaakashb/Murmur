# Contributing to Murmur

Murmur is pre-release. Use the root [README](README.md) for visitor setup and privacy information.

## Requirements

- An Apple Silicon Mac running macOS 14 or later (building needs the macOS 26 SDK), with Xcode Command Line Tools or Xcode and Swift 6.0 or later for the pinned helper package. The app compiles in Swift 5 language mode.
- Python 3 for the corpus and model scripts.
- Foundation-only modules and their checks also build on Linux; they were last checked with Swift 6.1.2.

## Build

Run commands from the repository root. The first build needs internet access to fetch the pinned FluidAudio package and Sparkle archive. It does not download speech model weights.

```sh
app/build.sh
open runtime/Murmur.app
```

`build.sh` builds the pinned `ModelBench` helper and the managed local cleanup helper (`app/tools/build_cleanup_runner.sh`: a pinned mlx-swift-lm build whose Cmlx target is the checksum-verified official prebuilt framework, because the Command Line Tools have no Metal compiler; the first run downloads it), both incrementally every run, compiles the app with `-swift-version 5`, and signs both executables with Hardened Runtime. Development builds use `clouddev Development` if present, otherwise ad hoc; release builds use the audio-input entitlement, while development builds also disable library validation so the local signing identity can load Sparkle. Set `MURMUR_SIGN_IDENTITY` to use another identity; a stable identity keeps macOS permissions across rebuilds.

For distribution, first configure the update feed and public key described in [update packaging](app/UPDATES.md). `MURMUR_RELEASE=1` also requires a valid Developer ID Application identity and an existing notarytool Keychain profile. It signs with secure timestamps, submits for notarization, staples and validates the ticket, and runs Gatekeeper assessment; any failed step stops the build.

```sh
MURMUR_RELEASE=1 \
  MURMUR_SIGN_IDENTITY='Developer ID Application: YOUR NAME (TEAM_ID)' \
  MURMUR_NOTARY_PROFILE='your-existing-notary-profile' app/build.sh
```

`runtime/` and `spike/.build/` are ignored by Git. Do not commit audio, transcripts, model files or API keys.

## Documentation

Keep tracked documentation useful to users or contributors: setup, privacy, security reporting, licensing, maintained tool instructions and reproducible measurements with limitations. Keep conversation logs, agent handoffs, local installation evidence and private audits outside the repository. Moving a tracked file or adding it to `.gitignore` does not make it private; deleted files remain in Git history.

## Publication checks

Run the redacted secret scans in [SECURITY.md](SECURITY.md) before sharing a branch or making a release. Review all fetched branches and PR refs, and hosted content separately before changing repository visibility. Keep private audit evidence outside this tree.

## Checks

The checks are offline or loopback and synthetic; they do not record audio, insert text or inject keys. Run the ones that cover your change, and all of them before a release, from the repository root:

```sh
app/check.sh                      # all checks
app/check.sh cleanup providers    # only these
app/check.sh --list               # names
```

`app/check.sh` holds each check's source list, which is that module's whole dependency set: add a source file there only when a check really needs it. `controller` and `meeting_start` compile the whole app and need macOS; `launch`, `history`, `recording` and `speaker_flow` also need macOS. The rest are Foundation-only and also build on Linux. The app itself is every `app/*.swift` except `main.swift` and `check_*.swift` (`app/sources.sh`), so a new source file needs no build-script edit. When using Command Line Tools with its macOS 27 SDK, the scripts select its macOS 26 SDK if installed; set `SDKROOT` to choose another.

Visual fixture (macOS, temporary storage, no capture or insertion):

```sh
app/check_ui.sh
app/check_ui.sh --light --compact --opaque
app/check_ui.sh --settings-search --sidebar-collapsed --snapshot /tmp/murmur-search-dark.png
app/check_ui.sh --settings-search --light --compact --snapshot /tmp/murmur-search-light.png
app/check_ui.sh --paste-failed --bubble-only
app/check_ui.sh --undo --bubble-only
app/check_ui.sh --light --insights
app/check_ui.sh --history-off
app/check_ui.sh --guide 4
app/check_ui.sh --meetings --compact --light
app/check_ui.sh --meeting-live
app/check_ui.sh --meetings --meeting-notes --meeting-settings --never --light --speaker-settings --ready
app/check_ui.sh --call-offer --saved --bubble-only
MURMUR_TIP_PNG=/tmp/murmur-tip.png app/check_ui.sh --tip-milestone --bubble-only --snapshot /tmp/murmur-tip-window.png
MURMUR_TIP_PNG=/tmp/murmur-tip-edit.png app/check_ui.sh --tip-voice-edit --bubble-only --light --snapshot /tmp/murmur-tip-edit-window.png
app/check_ui.sh --home-milestone --light
app/check_ui.sh --explore-dots --tab
app/check_ui.sh --meetings --speakers --meeting-notes
app/check_ui.sh --meetings --separating
```

Additional fixture flags and their side effects are described in [app/README.md](app/README.md). Standalone probes are in the [insertion spike](spike/README.md), [capture probe](module-one/capture/README.md) and [model tooling](module-one/models/README.md) guides. A synthetic fixture is not live capture, insertion, or provider evidence.

## Windows port

Murmur for Windows lives in `windows/` and is kept in step with the Mac app: change and test on the Mac first, then port. Run `app/check.sh windows_parity` after changing a ported file or corpus. It regenerates the golden and shared files the C# tests use and checks `windows/porting.json`. The checklist is in [windows/CONTRIBUTING.md](windows/CONTRIBUTING.md).

## Conventions

These are the patterns in the existing code and docs.

- Native Swift with Apple frameworks (Foundation, AppKit, SwiftUI, AVFoundation, Security). FluidAudio is the pinned Swift package; Sparkle is a separately pinned framework archive. Keep new logic Foundation-only where possible, so its checks run on Linux too.
- Small concrete types per concern (one file per module, such as `Cleanup.swift` or `OpenAICompatible.swift`), not a plugin framework.
- Checks are standalone executables named `check_<module>.swift`, compiled by `app/check.sh` with the files they test. They exit non-zero on failure and need no external network, real keys or microphone; `check_http.py` uses a loopback server with synthetic data.
- Code must compile under `-swift-version 5`, which `build.sh` uses.
- Treat dictated text as untrusted data. Never log transcript text, API keys or response bodies; error reasons are content-free enums.
- Store API keys only in the Keychain. Never send transcripts, audio, dictionary entries or keys in update checks.
- Keep transcription routes fixed on failure. Preserve the guarded local fallback described in [provider integration](app/PROVIDERS.md); do not add model or provider substitutions without an explicit product decision.
- Document claims with evidence. In Markdown docs, distinguish measured results from assumptions and keep prose plain and short. Report device results only for the configuration actually measured.

## Commits

- Make small logical commits after each working checkpoint.
- Use Conventional Commit subjects: `type(scope): summary`, imperative and lowercase, such as `feat(cleanup): add …`, `fix(ui): …`, `docs(providers): …`, `test: …`, `refactor(app): …`. The scope is optional.
- Merge commits describe what is merged in plain words, such as "Merge cloud providers, Keychain credentials and GitHub update checker".
- Write commit messages as a human and do not add Codex co-author trailers.

## Licensing

Murmur's source is MIT licensed ([LICENSE](LICENSE)); contributions are accepted under the same license. Add any new dependency or model to [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) after checking its license at the pinned version.

Sparkle builds and release asset preparation are documented in [app/UPDATES.md](app/UPDATES.md). Public updates need an accessible GitHub feed and the existing production signing key; Developer ID signing and notarization are separate release steps.
