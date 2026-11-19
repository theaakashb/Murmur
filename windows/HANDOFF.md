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
  - Being finished by worker 2 below: live words and chunked transcription.
- **M4: started.** Worker 3 is porting the core logic. The platform and UI parts haven't started:
  - website matching from the browser address bar through UI Automation;
  - the voice edit UI;
  - the learning observer (UI Automation read-back plus scoped keystrokes);
  - the welcome guide.
- **M5 and M6:** not started.

## Active workers (all Codex `gpt-6.2-sol`, high effort, mode full-access, on the Mac host)

| # | Agent ID | Worktree / branch | Assignment | Last known state |
| --- | --- | --- | --- | --- |
| 1 | `73cf71a2-227a-460f-aec8-28072dc6d481` | `/Users/aakash/.paseo/worktrees/01wxcxve/windows-ui-fluent` / `windows-ui-fluent` | Fluent redesign of Settings, History, the bubble and the tray. Add `Bubble.SetPartial`. Fix the **download freeze** with a regression test. | Before-screenshots done. Building the redesign. Will ask "need desktop" for the after-screenshots and a 6-dictation harness run. |
| 2 | `a7240d06-07ff-4539-90ca-6957ec171740` | `.../windows-m3-live` / `windows-m3-live` | Live words (`--stream` into the bubble) and chunked transcription (Chunker/ChunkJob port). | Committed `ba027ea` (clean). Currently holds the VM desktop for its last harness runs; will say "desktop free". |
| 3 | `660ac734-9568-4346-b5eb-fe8e342b5f42` | `.../windows-m4-core` / `windows-m4-core` | Core ports: EditPrompt, MurmurCloudEdit, VoiceEdit (logic), EditTrigger/EditShortcut, Tips, Insights, ProfessionalHint, WelcomeGuide, with tests and golden files. | Running; no report yet. |

**Desktop handoff:** when worker 2 says "desktop free", tell worker 1 "desktop is yours".

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

- **The app freezes while downloading a model** (user-reported on the PC). Assigned to worker 1. Must be fixed before M6.
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
