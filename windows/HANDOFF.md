# Murmur for Windows: coordinator handoff

Rewritten 2026-11-20 03:55Z by coordinator `f9e7f4a7-3580-4cf3-a339-f14b44fbf5d9`. Current coordinator, since 04:00Z: `268d6a36-00e7-40c3-a2bd-0b2dacfa5718`. Read this first, then `windows/PLAN.md` (the Status section is current), `windows/CONTRIBUTING.md` and `~/.claude/CLAUDE.md`.

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

- **M1, M2, M3: done.** Records are in PLAN.md Status and `windows/harness/README.md`. M3 live words and chunked transcription: `8535cb1`.
- **M4: code complete; the exit needs the user's PC.**
  - Core: `4aab0c6`.
  - Website matching: `fcceafc` and `8b1b363`. Edge and Chrome pass the VM matrix at 125%. Firefox isn't verified (the probe couldn't foreground its window), and Brave wouldn't install on the VM.
  - Learning observer: `67e4ed3`. VM interactive tests 3/3: readable Notepad, password, unreadable and hook.
  - Voice edit and learning extras: `10cbd2a`. Notepad 2/2 with a fake provider.
  - Welcome guide: `de97c81`.
  - Still to verify on the PC: the browser matrix (including Firefox and Brave), learning on real readable and unreadable fields, voice edit against a real provider.
- **M5: code complete; the exit needs the user's PC.**
  - Core: `5ddfdc6`.
  - Platform (process loopback, session enumeration, recorder): `c6c76be`. Loopback activation works on the VM without audio devices.
  - Speakers and cloud route: `5c49c06`. Speaker separation uses pinned pyannote-seg-3.0 and WeSpeaker ONNX models in the speech helper; it worked on a 16 s Mac sample, and its 0.6 threshold is untuned.
  - The Meetings UI is in progress (UI worker).
  - Exit: a real 30-minute Teams or Zoom call, following `windows/INSTALL-TEST.md` "Meetings".
  - Note for the Mac app (not fixed; `main` is out of scope): the M5 worker found a latent race in Swift. A meeting can look finished before its last chunk's answer is applied, so speaker separation can start without that audio.
- **M6: local part done (`ce410d2`).**
  - Velopack 1.2.161 pinned. The Updater (GithubSource, `MURMUR_UPDATE_FEED` for local feeds), LoginItem (on at the first installed launch, as on the Mac), `windows/tools/package.ps1`, the `.github/workflows/windows-release.yml` draft (`workflow_dispatch`; sign and upload are off) and `windows/UPDATES.md`.
  - On the VM: install, delta update and uninstall kept user data byte-identical. The uninstall leftover Run value is fixed.
  - Not exercised: `RestartToUpdate()`; whether the client actually applied the delta.
  - Needs the user: the signing account, the vpk signing flags (unverified for 1.2.161), and publishing (manual, `windows-v*` tags, never latest).
- **UI workstream:** the Windows UI matches the Mac look (user direction 2026-11-19).
  - Pass 1, `2abe6ca`: shared window and sidebars, underline tabs, Mac History.
  - Pass 2, `de97c81`: switches, (?) icons, column width, Modes/Dictionary/Cues, welcome guide.
  - Pass 3 is in progress.

## Active workers (03:55Z)

| Agent | Model | Worktree folder / branch | Assignment |
| --- | --- | --- | --- |
| `33161254-7ebc-4901-8f17-7ea1f29d70a9` | Claude Sonnet 5.5, medium | `.../windows-m4-core` / `windows-ui-meetings` (from `de97c81`) | UI pass 3. Meetings screen and sheets; call offer panel (`CallOffered`); welcome guide mic privacy (read-only Platform helper) and test dictation (`Controller.Setup.cs` if needed); Dictionary suggestions and reset; the speaker model rows and switches (`meetingSpeakers`, `meetingRoomSpeakers`, `RefreshSpeakerModels()`); an Updates pane, Open at login, tray "Restart to update". Then merge `origin/windows`, re-capture, and run the 6-dictation harness on the merged build. |

All other workers are archived. Free workspaces to reuse:
- `wks_50c7ffeb2fda107c`, folder `windows-m3-live`, branch `windows-m6-velopack` (merged);
- `wks_f5364d96a43c462c`, folder `windows-ui-fluent`, branch `windows-m5-speakers` (merged).

Use a fresh branch from `windows` for each new task.

**Usage at 03:53Z:**
- Claude: 5h 57% (resets 04:40Z), weekly 26%. **User override: ignore the 20% Claude threshold.** Still checkpoint at each step.
- Codex: 5h 13% (resets 07:37Z), weekly 58%. Use Codex again after the reset.

**Merge checklist:**
1. Check the diff stays within the worker's ownership.
2. On the Mac: `dotnet build windows/Murmur.Windows.slnx` (0 warnings), `dotnet test`, and `check_parity.py`.
3. On the VM: copy the merged tree with `git archive HEAD windows app module-one/models/artifact-pins.json | ssh murmur-win "tar -x -C C:/src/w-coord -f -"`. Then run `dotnet publish windows/src/Murmur.App -c Release -r win-x64` (trimmed; it catches CsWinRT1032) and `dotnet test windows/Murmur.App.slnx`. Then delete `C:\src\w-coord`.
4. Merge with `--no-ff`, push `windows`, and check CI.

**VM desktop:** one worker at a time, handed out by the coordinator. App-only WinUI bitmap capture works through `/tmp/murmur-maclook/capture-bitmap.ps1`.

**VM disk:** C: is small. Keep it above 1.5 GB, and workers clean their own `C:\src\w-*`. Keep `C:\src\harness-run-*` (M2 evidence), `C:\src\Murmur` (fixture WAVs) and `C:\src\models`.

## Open bugs

- **CI is red since `4aab0c6`** (last green run `35d640d`). Five Murmur.Platform.Tests fail on windows-latest: the no-device audio tests and LearningTests.ReadableField. Because of this, no `Murmur-windows-x64-unsigned` artifact gets built. Worker `d3e95f6e` is fixing it on `windows-ci-fix` (assigned 04:10Z).
- **The PC Paseo host `srv_XXiOqPoelqaJ` was offline at 04:05Z.** Staging app-next is still to do.

- **VM SSH drops (twice, about 02:00Z, 2026-11-20):** sshd resets at `ssh-userauth`. Both times it came right after Chrome probe runs, and it recovered on its own without a reboot. Afterwards: 4.6 GB disk and 6.8 GB RAM free, no chrome.exe left. Cause unknown. Avoid long browser-probe loops, and close browsers by PID.

- **The app freezes while downloading a model:** fix merged in `c8dd54f`. Progress is now sampled at 10 Hz with one pending UI update, and status and removal work run off the dispatcher. Verified on the VM with a synthetic transfer. Still to confirm: a real download on the user's PC.
- **Golden check "hang": not a bug.** A full `generate.sh --check` takes about 15 minutes, and longer under load; all 21 generators pass. Run it per generator name.
- **Notepad paste on the unattended PC:** passed 1 of 3 runs in the earlier smoke test. The user's own real-mic run in Notepad worked. Watch for it, but it's not reproduced with a person present.
- **Hotwords:** one correct made-up name was respelled in the spike (`Kestrelmoor`). Boosting is on when the dictionary has entries. The CTC rescoring alternative is still open.

## Needs user

- **M4 and M5 exits on the PC:** the user-run steps are in `windows/PC-CHECKLIST.md`.
  - the browser matrix: Chrome, Edge, Firefox, Brave;
  - learning on real fields;
  - voice edit against a real provider;
  - a 30-minute meeting following `INSTALL-TEST.md` "Meetings";
  - a real model download (the freeze fix).
  
  Install the latest CI artifact or `T:\Paseo Project\Murmur` app-next after the next build.
- **M6:** the signing account, the test-repo release runs and publishing. The setup steps are in `windows/UPDATES.md`, "Needs user". The pinned vpk 1.2.161 still needs signtool and the dlib; Velopack 1.3 or later signs directly.
- **Velopack 1.3:** the user asked for stable 1.3.x, but none exists. The only 1.3 build is the prerelease 1.3.39-g8d2844f; the latest stable is 1.2.161. Decided: stay on 1.2.161, and move to 1.3 when a stable release ships (UPDATES.md). The user may still override.
- **Optional:** a bigger VM disk, so parallel builds don't run low.

## Next actions

1. Review and merge UI pass 3 (`windows-ui-meetings`) with the checklist; look at the pairs yourself.
2. Build a fresh unsigned app for the user's PC (CI artifact `Murmur-windows-x64-unsigned`) and give the user the M4 and M5 PC checklist. Don't touch the PC while the user is testing.
3. Remaining parity gaps to schedule, on Codex after 07:37Z:
   - settings search highlight;
   - Firefox reader activation;
   - the RestartToUpdate tray path;
   - speaker threshold tuning on real calls;
   - Insights charts and the streak calendar;
   - the Home screen against the Mac;
   - the voice edit shortcut row (Mac `VoiceEditShortcutSettings`). Windows has the `voiceEditShortcut` key but no Settings UI for it.
4. M6 with the user: signing, then a test-repo release, then the first `windows-v*` draft.
