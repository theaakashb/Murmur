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

## Active workers (Claude Sonnet 5.5, medium effort, bypassPermissions, on the Mac host; started 2026-11-20 ~01:58Z at the user's request while Codex's 5h window was at 5%)

The Codex workers `73cf71a2`, `a7240d06` and `660ac734` are archived. Worktree folders were reused for Paseo workspaces, so folder names no longer match branches.

| # | Agent ID | Worktree / branch | Assignment | Start point |
| --- | --- | --- | --- | --- |
| A | `89746ba4` (archived) | `windows-m4-core` | M4 core ports. | **Merged** as `4aab0c6`: 22,609 Mac tests, 75/75 parity, goldens the same. Still open: Tips/ProfessionalHint persistence isn't compared with Swift UserDefaults; the EditShortcut reserved lists are app-supplied; the IVoiceEditTarget/EditChord seams are tested only with fakes. Next: the voice edit and welcome guide app/UI wiring (Codex, after the reset, with the Mac-look UI rework). |
| B | `c90225b7-881f-4b75-b91f-e14cf650764d` (idle) | `.../windows-m3-live` / `windows-m4-sites` | Website matching. | Checkpoint committed locally; not merged yet. The Chrome failures weren't the reader: the window wasn't foregrounded (test fixed with AttachThreadInput/SwitchToThisWindow), and one Esc left the omnibox focused (test now presses Esc twice). After the fixes the normal read always matches, and one full pass matched every case. Still open: intermittent 'address bar did not take focus' after Ctrl+L (probe timing). Firefox and Brave not run. Next owner: Codex. |
| C | `774c3946-7c5e-4143-87f1-411be3e0c079` (idle) | `.../windows-ui-fluent` / `windows-m4-learning` | Learning observer. | Code complete, local commits `c13d175` and `f4ac58f`. Mac tests pass (22,250). **Blocked on the VM:** the Windows build/publish/App tests haven't run, nor have the readable and unreadable field tests. Known gaps: clicks are detected with GetAsyncKeyState (can miss; the focus check is the backstop); no History `AfterPaste` record and no tips update; no dictionary refresh and no "learned" notice. |

**Queued (no worker yet):** the UI rework to match the Mac app's look (PLAN.md "UI workstream"; user direction 2026-11-19). It starts on a Codex worker after the Codex reset (schedule `aae94043`, 02:40Z) when a slot is free.

**Handback to Codex after the reset:** the Claude worker commits a checkpoint and stops, then a new Codex worker takes the branch. One owner per branch at a time.

**Claude usage rule:** at ≤20% weekly, start no new Claude work; at ≤10%, checkpoint. It was 29% weekly at 01:58Z.

**Desktop:** worker B holds it. Worker C asks for it after B.

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
