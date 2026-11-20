# Murmur for Windows: coordinator handoff

Written 2026-11-19 by the outgoing coordinator. Read this first, then `windows/PLAN.md` (the Status section is current), `windows/CONTRIBUTING.md` and `~/.claude/CLAUDE.md`.

## Objective

A native C#/.NET 10 + WinUI 3 port of the Mac app, in `windows/` on branch `windows` of `theaakashb/Murmur` (public). V1 is full parity: dictation, cleanup and routes, Murmur Cloud, dictionary and learning, modes, voice edit, history, local speech and cleanup models, meetings, and an updater.

Milestones: M1 core, M2 shell and cloud dictation, M3 local models, M4 modes, voice edit and learning, M5 meetings, M6 release, updater and signing. The UI workstream runs across all of them.

## Decisions (details in PLAN.md "Decisions already made")

**Insertion and keys**
- Paste only, with no typing fallback.
- Right Ctrl is hold-to-talk. Ctrl+Win+Shift+V pastes the last dictation.
- The welcome guide detects a missing Right Ctrl.

**Models and data**
- Speech: Parakeet Unified ONNX through sherpa-onnx.
- Cleanup: Vedu Scribe GGUF Q8_0 through LLamaSharp, published at `clouddevaakash/vedu-scribe-0.8b-gguf@edf27597`.
- The lexicon is a bundled SCOWL-60 word list.
- Data lives in `%LOCALAPPDATA%\Murmur`; uninstall keeps it.

**Platform and release**
- Windows 11 22H2 or later, x64 first.
- Velopack for install and updates, from GitHub releases. Windows-only releases are tagged `windows-v*` and never marked latest, because the Mac appcast follows latest.
- Azure Trusted Signing.

**Process rules**
- Replace features outright. No fallbacks or back-compat shims.
- Shared data is exported from the Swift code. Parity is checked with golden files, `porting.json` and `check_parity.py`.

**Acceptance**
- M3 quality acceptance is the user's judgment by ear: Windows dictation is equal to the Mac's. There is no measured real-speech WER.

## Guardrails

- **Git:** push only to `windows`. No main, no PRs, no merges to main.
- **Privacy:** never print or commit keys, transcripts, History contents, audio, model weights or screenshots. The desktops can show personal windows.
- **Concurrency:** at most 3 active workers.
- **Usage:** run `paseo usage --json` at least every 30 minutes.
  - At 20% or less in any window, pause new work and prefer the other provider.
  - At 10% or less, write a checkpoint into PLAN.md Status, push, and stop.
- **Needs user:** park anything that needs the user and continue other work. Report at each milestone exit or blocker.
- **Star Labs PC rules** (from the user):
  - Every install and all project work goes under `T:\Paseo Project\` (the drive labelled "Tools").
  - Read-only everywhere else.
  - No keys stored by agents. No system setting changes.
  - The app's data goes in `%LOCALAPPDATA%\Murmur` on C:; the user approved that.
  - Never restart or replace the app while the user is testing.

## Hosts

### This Mac
- **Worktree:** `/Users/aakash/.paseo/worktrees/01wxcxve/windows`. The SDK is at `~/.dotnet` (`export PATH="$HOME/.dotnet:$PATH"`).
- **Venv:** `~/.venvs/murmur-convert`.
- **Model work** in `~/murmur-m3/`:
  - `dl/` holds the pinned downloads.
  - `out-final/` holds the GGUFs.
  - `bench/` holds the Mac ASR baseline.
- The Mac app's helpers are in `/Users/aakash/Local Documents/paseo/Murmur/runtime/Murmur.app/Contents/MacOS/`.

### Windows VM
- **Access:** `ssh -o BatchMode=yes murmur-win`. Windows 11 23H2, a QEMU virtual CPU, no audio devices, about 8 GB free disk.
- **Shell:** cmd. Use `powershell -NoProfile -Command`, with `$env:Path` rebuilt from the machine and user values first.
- **Checkout:** `C:\src\Murmur` (branch `windows`).
- **Models:** `C:\src\models\{speech,cleanup}` for harness side-loading. Workers use `C:\src\w-*` folders.
- **GUI:** runs only in the logged-in session, through `C:\src\runui.ps1 -Exe X -Wait N` or `C:\src\runps.ps1 -Script X -Wait N`. Simulated input works there.
- **Desktop:** one worker at a time. The coordinator hands it over: "desktop is yours" / "desktop free".
- **Settings changed:** clipboard history is on (it already was).

### Star Labs PC
- **Access:** Paseo host `srv_XXiOqPoelqaJ`, through `create_agent` with that serverId and workspace `wks_3d3a18a1cff53984` (cwd `C:\`). SSH doesn't reach it. `respond_to_permission` can't reach remote agents, so create them in mode `auto`.
- **Machine:** Windows 11 Pro 26200, Ryzen 9 5900X, 32 GB, a real microphone (plugged in by the user). The user is logged in as `starlabs\aakash`.
- **Layout under `T:\Paseo Project\Murmur\`:**
  - `src`: a clone of `windows`, at `2dec66b` or later;
  - `app`: the first build;
  - `app-next`: the build with the cleanup-model download (`2dec66b`); the user was told how to switch to it;
  - `tools\dotnet`: the .NET 10.0.404 SDK;
  - `cache`;
  - `models`: empty.
- **Cache environment:** set `NUGET_PACKAGES`, `DOTNET_CLI_HOME` and `TEMP` to `T:` paths, in each process only.

## Milestones

- **M1: done.** Core text engine, golden parity, CI green.
- **M2: done.**
  - Synthetic exit on the VM: 20/20 (`windows/harness/README.md`, "Result on record").
  - The user confirmed the real-mic run on the PC, in Notepad and other apps (not named).
- **M3: done.**
  - Synthetic key-free exit on the VM: 20/20, with local ASR and local cleanup. Median 2.5 s, max 8.3 s on the virtual CPU.
  - Synthetic WER: 9.02% ONNX vs 8.20% Core ML.
  - Cleanup parity with MLX: 399/400, with equal guard results.
  - The user accepted the quality by ear.
  - Live words and chunked transcription merged as `8535cb1` (VM key-free harness 20/20, 29.65 s fixture 10.3 s → 8.1 s release-to-paste). Not yet checked: real mic, cloud-route preview, Settings download flow in the live UI.
- **M4: in progress.** The core logic is merged (`4aab0c6`) and website matching (`fcceafc`). The platform and UI parts haven't started:
  - website matching from the browser address bar through UI Automation;
  - the voice edit UI;
  - the learning observer (UI Automation read-back plus scoped keystrokes);
  - the welcome guide.
- **M5 and M6:** not started.

## Active workers (as of 2026-11-20 03:10Z)

| # | Agent ID | Worktree folder / branch | Assignment | Start point |
| --- | --- | --- | --- | --- |
| UI | `33161254-7ebc-4901-8f17-7ea1f29d70a9` (Claude Sonnet 5.5) | `windows-m4-core` / `windows-ui-meetings` | Pass 3: the Meetings screen (MeetingsUI.swift) with sheets replacing the message boxes, the call offer panel (CallOffered), the welcome guide's microphone privacy state (a read-only Platform helper) and test dictation (`Controller.Setup.cs` if IsMurmur refuses), Dictionary suggestions and reset, re-capture, harness. | `de97c81`. Pass 2 merged: Mac switches (blue-on, per `.tint(Palette.accent)`), circled (?), 700 px column, Modes/Dictionary/Cues tabs, the welcome guide on first launch. |
| M5 next | `de4d6235-00f4-448e-87ad-1eb15492aa49` (Claude Sonnet 5.5) | `windows-ui-fluent` / `windows-m5-speakers` | Speaker separation (diarizer model pinned and downloaded, in the helper), meetings on the cloud route if the Mac has it, ResetMeetings on Delete All/Restore, quit confirmation during a meeting. | `c6c76be`. Platform merged: process loopback (activation works on the VM without audio devices), session enumeration, MeetingRecorder in Core, tray Start/Stop meeting. Real audio untested; the PC checklist is in INSTALL-TEST.md "Meetings". |
| M6 local | `68cee5b1-a6bc-46bc-b172-569af8fee551` (Claude Sonnet 5.5) | `windows-m3-live` / `windows-m6-velopack` | Velopack 1.2.161 pinned, Updater, LoginItem, package.ps1, draft workflow, UPDATES.md (`cf241ad`). Waiting for the desktop to run the VM install → delta → uninstall test. | Hooks for the UI: `App.Updater.ReadyVersion`, `RestartToUpdate()`, `UpdateReady`, `Updater.Explanation`, `LoginItem.IsOn` and `LoginItem.Set(bool)`. |

**Merged this session (all pushed to `windows`):**
- live words `8535cb1`;
- Fluent UI and the download-freeze fix `c8dd54f`;
- website matching `fcceafc` and `8b1b363`;
- M4 core `4aab0c6`;
- learning `67e4ed3`;
- voice edit and the learning extras `10cbd2a`;
- M5 core `5ddfdc6`;
- the Mac-look UI, pass 1, `2abe6ca`;
- the M5 platform `c6c76be`;
- the Mac-look UI, pass 2 (with the welcome guide), `de97c81`.

The Codex workers `011b034c`, `b84929e5` and `c281bc63` are archived.

**M5 core (`5ddfdc6`):** the meetings chunk feed and recovery, store, notes (Custom Provider) with the corpus checks, export with exact escaping, speakers, and the call detector. Not verified: live capture, call-process enumeration, diarization, real provider calls. Checkpoint `~/murmur-m3/checkpoints/m5-core.md`.

**User override (2026-11-20):** ignore the 20% Claude weekly threshold; the user resets Claude themselves. Keep HANDOFF and the worker checkpoint commits current at each step, and use Codex too after its reset.

**Usage, 03:10Z:** Codex 5h at 15% (resets 07:37Z), weekly 58%. Claude 5h at 81%, weekly 28%. One Claude worker only, to conserve the weekly allowance. At Claude weekly ≤20%, stop starting Claude work. New work goes to Codex after 07:37Z.

**Next (Codex after the reset, or Claude while above 20% weekly):**
1. The M5 platform: process-loopback capture (COM interop, silence fill), WASAPI session enumeration for call detection, and the meetings UI in the Mac look. The VM has no audio devices, so the real test is on the user's PC.
3. The M4 exit on the user's PC: Firefox and Brave website matching, and learning on readable and unreadable fields.

**Before merging App changes:** run the VM `dotnet publish` (trimmed) as well as `dotnet test windows/Murmur.App.slnx`. The Mac solution doesn't build Murmur.App, and the Mac doesn't catch Windows-only test failures such as time zone IDs or accent folding under InvariantGlobalization.

**Ops:** the VM's C: drive dropped to about 700 MB during parallel builds. Workers clean their own `C:\src\w-*` outputs and keep more than 1.5 GB free.

**Desktop:** free; only the UI worker uses it.

**Pause, 2026-11-19 23:45Z:** Codex's 5h window is at 10% (it resets 02:31Z); Codex weekly is 72%. Claude's 5h window is at 100%, weekly 29%. All Codex workers are paused. The one-shot Paseo schedule `aae94043` (02:40Z) wakes the coordinator to resume them. On resume: `paseo usage --json --refresh`, then resume workers 1, 2 and 3 from their checkpoints.

**CI note:** `dotnet publish` (trimmed) catches CsWinRT1032 (collection expressions targeting `IEnumerable`), which `dotnet build` and `dotnet test` miss. Run the publish on the VM before merging App changes.

**Coordinator:** agent `f9e7f4a7-3580-4cf3-a339-f14b44fbf5d9`, which took over from `37717132` on 2026-11-19.

**Before merging each branch:**
1. Check the diff stays within the worker's file ownership: `git diff --name-only fdc05dc <branch>`.
2. On the Mac: `dotnet build windows/Murmur.Windows.slnx` (0 warnings) and `dotnet test`. Run `windows/golden/generate.sh --check <names>` per generator; a full run takes about 15 minutes.
3. On the VM: `git pull`, then `dotnet test windows/Murmur.App.slnx`.
4. For workers 1 and 2: rerun `windows\harness\run-exit.ps1`, plus `-LocalOnly` for 2. The fixture WAVs aren't in git; they're on the VM in `C:\src\Murmur\windows\harness\fixtures`.
5. Expect conflicts between workers 1 and 2 in `Bubble.cs` (`SetPartial`) and `SettingsWindow.cs` (the live-words row and toggles). Keep worker 1's UI and worker 2's behaviour.
6. Add the ported Swift files to `windows/porting.json` and run `python3 windows/tools/check_parity.py --update`.
7. Look at worker 1's screenshots in `/tmp/murmur-ui-shots/`. Never commit them.
8. Merge with `--no-ff` and a plain-words message, then push to `windows` and confirm CI is green.

## Open bugs

- **VM SSH drops (twice, about 02:00Z, 2026-11-20):** sshd resets at `ssh-userauth`. Both times it came right after Chrome probe runs, and it recovered on its own without a reboot. Afterwards: 4.6 GB disk and 6.8 GB RAM free, no chrome.exe left. Cause unknown. Avoid long browser-probe loops, and close browsers by PID.

- **The app freezes while downloading a model:** fix merged in `c8dd54f`. Progress is now sampled at 10 Hz with one pending UI update, and status and removal work run off the dispatcher. Verified on the VM with a synthetic transfer. Still to confirm: a real download on the user's PC.
- **Golden check "hang": not a bug.** A full `generate.sh --check` takes about 15 minutes, and longer under load; all 21 generators pass. Run it per generator name.
- **Notepad paste on the unattended PC:** passed 1 of 3 runs in the earlier smoke test. The user's own real-mic run in Notepad worked. Watch for it, but it's not reproduced with a person present.
- **Hotwords:** one correct made-up name was respelled in the spike (`Kestrelmoor`). Boosting is on when the dictionary has entries. The CTC rescoring alternative is still open.

## Needs user

- Names of the other apps used in the M2 real-mic run, only if the exit record needs them.
- Approval to start M5 and M6 work that touches external services: signing account setup, and publishing releases. Publishing stays manual.

## Next actions

1. When the workers report, review and merge them as above.
2. Then the M4 platform and app work:
   - website matching through UI Automation, using the browser test matrix in PLAN.md;
   - the voice edit UI;
   - the learning observer;
   - the welcome guide with the Right Ctrl step;
   - UI workstream passes on these screens.
3. Then M5 meetings (process loopback, call detection, notes, export with Markdown escaping, diarization), then M6 (Velopack, signing, release workflow, docs).
