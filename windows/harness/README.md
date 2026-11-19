# M2 exit harness

Automates the M2 exit check from `windows/PLAN.md` as far as a machine without a microphone or real accounts can: 20 synthetic dictations into Notepad, Edge and VS Code, with the clipboard, clipboard history and insertion status verified for each one. It runs on a Windows 11 machine with a logged-in console session (the VM, or the user's PC).

## Run it

On the Windows machine, from any shell (an SSH session works; the GUI parts run in the console session through scheduled tasks):

```
powershell -NoProfile -ExecutionPolicy Bypass -File <checkout>\windows\harness\run-exit.ps1
```

Options: `-LocalOnly` (key-free, see below), `-SeedDictionary`, `-ModelSource <dir>`, `-Count 20` (dictations), `-AppExe <path>` (use this Murmur.exe and skip the app build), `-SkipAppBuild`, `-OutBase C:\src` (where `harness-run-<timestamp>` goes).

Needs: .NET 10 SDK, Edge, VS Code (user install), an unlocked console session. Re-runnable: each run gets a fresh data folder. It stops leftover `Murmur`, `FakeProvider` and `Driver` processes first (by name), so do not run it on a machine where you use Murmur.

The run folder `C:\src\harness-run-<timestamp>` holds `results.json`, `summary.txt`, `driver.log`, `provider-requests.json` (content-free), `screenshot-<target>.png`, and the app's data folder (`data\`, synthetic text only, including `diagnostics\events.jsonl`). The exit code is 0 only if every dictation passed every check that could be verified.

## What runs

| Piece | What it is |
| --- | --- |
| `fixtures/` | 23 synthetic WAVs plus `manifest.json` (see its README) |
| `FakeProvider/` | Loopback-only (127.0.0.1) server: `POST /v1/audio/transcriptions`, `POST /v1/chat/completions`, `GET /v1/models`, `POST /control/next {"id"}`, `GET /control/requests`. Logs multipart field names and audio byte counts only. `dotnet run --project FakeProvider -- --selftest` checks it. |
| `Driver/` | Session-1 UI driver: launches each target, presses Right Ctrl with `SendInput`, reads back through UI Automation, watches the clipboard, reads Win+V history. |
| `pages/textarea.html` | The `file://` page with a `<textarea>` that Edge opens. |
| `run-exit.ps1` | Builds, starts the fake provider, writes `settings.json` (Custom Provider on the fake for transcription and cleanup, Polish on, `automaticInsertion` on, `shortcut` `right-ctrl`), launches `Murmur.exe` with `MURMUR_DATA_DIR`, `MURMUR_SYNTHETIC_AUDIO`, `MURMUR_TEST_EVENTS=1` through a launcher `.cmd` in session 1, runs the driver, collects results, cleans up. |
| `StubApp/` | A stand-in `Murmur.exe` that implements the three test hooks, only to test the harness itself before the real app exists. Not part of the exit run. `-AppExe StubApp\bin\Release\net10.0-windows10.0.19041.0\Murmur.exe` runs the whole harness against it. |

Targets: Notepad (7 dictations), Edge textarea (7), VS Code with a new untitled file (6). VS Code is the Electron app; a second Electron app was not added.

## Per dictation

1. Clear the editor, put `sentinel-<guid>` on the clipboard, write `next.txt`, `POST /control/next`.
2. Hold Right Ctrl for the WAV duration + 0.4 s, release.
3. Wait up to 20 s for the `paste` event in `events.jsonl`.

| Check | What it proves |
| --- | --- |
| `paste-event` | The whole path ran: hook, synthetic audio, transcription and cleanup through the provider, paste. An `error` event fails it with its stage. |
| `insertion-status` | The app's status is `verified` or `unverified`, never `failed`. |
| `history-entry` | The History entry named by `historyId` exists with `finalText` (the text the next check expects). |
| `readback-contains-final-text` | The target's own text field, read through UI Automation, contains the History `finalText`. This is the proof the text really landed in a real editor. |
| `cleanup-matches-manifest` | Advisory: the app pasted the fake provider's cleanup unchanged (shows the guard or fallback did not alter it). |
| `clipboard-restored-event`, `clipboard-holds-sentinel` | The `clipboard-restored` event came, and the clipboard is the sentinel again within 2 s (no lost clipboard). |
| `staged-item-has-exclusion-formats` | A clipboard format listener (`AddClipboardFormatListener`, format names only, never text) saw the dictation on the clipboard and that item carried `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory` and `CanUploadToCloudClipboard`. |
| `sentinel-restored-seen` | The restore also appeared as a clipboard change. |
| `history-has-no-dictation-text` | If clipboard history is on, `Clipboard.GetHistoryItemsAsync` has no item containing the dictation. If it is off or unreadable, the check is reported `skip` as "not verifiable on this machine" and the format check above is the evidence. |

Read-back method per target: UI Automation `ValuePattern` on the Notepad document, the Edge `textarea` (name `dictation`), and VS Code's editor input (`native-edit-context`). VS Code is launched with a throwaway profile, `--force-renderer-accessibility`, `editor.accessibilitySupport` on and AI features off (a Chat box would otherwise take the focus; the driver only accepts the editor named `Untitled-…`), and it declines the first-run sign-in and theme screens. All three targets are opened before the first dictation and kept open: closing one hands the foreground to the next window in z-order, and on the VM that is an elevated PowerShell, which injected input cannot reach. If an editor ever exposes no text, the driver falls back to select-all + copy, used only for verification, and says so in the result.

`release-to-paste latency` is the time from key release to the app's `paste` event timestamp. With the stub app it is about 0.3 s (the stub's own 150 ms wait included), so it is a harness check, not a performance claim.

## Driver dev commands

`Driver selftest-targets <dir> [notepad|edge|code]` types and pastes a known string into each target and reads it back (no app needed). `Driver selftest-clipboard` checks the format listener. `Driver probe <target> <dir>` dumps the UIA elements. Run these in session 1 (`C:\src\runps.ps1` on the VM).

## What the harness cannot prove

- Real microphones: levels, device selection, Bluetooth headsets, privacy prompts and permission denial, device unplugging. The audio is a WAV fed in through the test hook, bypassing WASAPI capture.
- Real provider latency and errors: the fake answers in microseconds and never fails, rate-limits or returns a bad response.
- Teams, Slack and other apps with their own editors, sign-in or rich-text handling. Only Notepad, a browser textarea and VS Code are covered; M2's exit still needs a manual pass in Teams and Slack.
- Elevated windows: pasting into an administrator window is blocked by UIPI, and this harness does not test that message or recovery. (On the VM an elevated PowerShell sits in the console session; the driver works around its foreground lock.)
- IME, dead keys and non-US layouts: the driver types and pastes plain text and the hold key is a fixed key code.
- Multi-monitor and DPI: one monitor, one scale.
- Real Win+V behavior when clipboard history is off: the history check is skipped and only the exclusion formats are asserted; the formats are not proof that Windows honors them in every build.
- Injected input differs from a real keyboard in one way that matters: the app's keyboard hook must not ignore `LLKHF_INJECTED` events, so a hook that filters injected keys will fail here while working for a person (and a hook that does not filter them cannot be distinguished here).
- Anything after the first 20 dictations: long sessions, memory, history retention, update, sleep and resume.

## Result on record

2026-11-19, VM `murmur-win` (Windows 11 23H2, build 22631), commit `a772ab8`:
- **Dictations:** 20/20 passed (Notepad 7, Edge 7, VS Code 6).
- **Every hard check passed:**
  - paste event and insertion status (all `verified`);
  - History entry;
  - UI Automation read-back of the final text;
  - clipboard restored to the sentinel;
  - all three exclusion formats on every staged item;
  - no dictation text in Win+V history (clipboard history on).
- **Release-to-paste latency** against the loopback fake provider: min 270 ms, median 293 ms, max 609 ms.
- **The advisory check** shows the strict guard rejected 8 of the fake rewrites and inserted the local-rules fallback: 3 `numberAdded`, 3 `structureAdded`, 2 `unexplainedDeletion`. This is the guard ported from the Mac and checked against its golden outputs.

## Key-free run (`-LocalOnly`)

`run-exit.ps1 -LocalOnly` writes `transcriptionRoute` `local` and `cleanupChoice` `local-model` (Polish on) and copies the pinned model files from `-ModelSource` (default `C:\src\w-integrate\models`, with `speech\` holding `encoder/decoder/joiner.int8.onnx` and `tokens.txt`, and `cleanup\` holding `vedu-scribe-0.8b-Q8_0.gguf`; never committed) into the run's `data\Models\<model>-<rev7>` folders. The app verifies them against the pins, the speech helper and the cleanup helper run from `Speech\` and `Cleanup\` beside `Murmur.exe`, and the run fails if the fake provider sees any request. The side-loaded copies are deleted at the end of the run (1.5 GB). The WAV fixtures must be in `fixtures\` (generate on a Mac, see its README). `-SeedDictionary` also writes a dictionary so the speech helper runs with `--vocabulary`.

Result 2026-11-19, VM `murmur-win`: 20/20, every hard check passed, 0 provider requests, 18 cleanups `modelAccepted` and 2 `unexplainedDeletion` guard fallbacks. Release-to-paste latency (CPU only, no GPU): min 1655 ms, median 2616 ms, max 6766 ms (the first dictation includes loading both models). With `-SeedDictionary`: 20/20, min 1887, median 3023, max 6424 ms; raw transcripts differed from the unseeded run in punctuation only, as two unseeded runs differ from each other.

## Real-microphone confirmation

See [../INSTALL-TEST.md](../INSTALL-TEST.md): install on a real PC, then do 20 cloud dictations (M2) and the local-model real-speech comparison (M3).
