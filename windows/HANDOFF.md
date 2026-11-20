# Murmur for Windows: coordinator handoff

**Current coordinator: Coordinator 4 (`b080c3f1-b10a-47bb-9925-44e541f682ff`), from 2026-11-20 09:40Z. Workers report to it.**

Refreshed 2026-11-20 about 09:50Z by coordinator 3 (`268d6a36-00e7-40c3-a2bd-0b2dacfa5718`; it took over from `f9e7f4a7` at 04:00Z). The orchestrator is `8f691026-6ec3-41d4-8c14-4cba2ef6db44`. Read this first, then `windows/PLAN.md` (the Status section is current), `windows/CONTRIBUTING.md` and `~/.claude/CLAUDE.md`.

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
  - User override: keep using Claude past the 20% threshold; the user resets it. Use Codex when it has headroom.
  - Otherwise, at 20% or less in any window, pause new work and prefer the other provider.
  - At 10% or less, write a checkpoint into PLAN.md Status, push, and stop.
- **Providers (user directive, 2026-11-20 about 10:10Z):**
  - Prefer Codex workers for all work while Codex has headroom.
  - When Codex reaches 10% or less in its 5h or weekly window, or hits rate-limit errors, switch the affected workstreams to Claude (Sonnet by default) at once and keep going.
  - Hand over through checkpoint commits, with one owner per branch.
  - Switch back to Codex after its reset.
  - The Claude override still stands: keep working past 20%.
  - The UI polish worker (Sonnet) was already mid-task, so it finishes on Claude; new work starts on Codex.
- **Mac UI reference (user, 2026-11-20):**
  - `/Applications/Murmur.app` (0.3.3 build 37, tag v0.3.3 = `c70cdb1`; main is 7 commits ahead with backup/meetings-export/CI/docs fixes, and `67b37a7` is included) and is the reference for UI behaviour.
  - Check its Info.plist version and build against origin/main first, and report any mismatch.
  - Observe only. Don't change its settings; don't read History, dictations, dictionary or keys; don't quit or restart it.
  - `screencapture` from agents gives a black frame, and the panel PNG hooks drop glass, shadow and animation.
  - For animation, use `app/check_ui.sh --snapshot` fixtures, or ask the orchestrator for a recording made by the user.
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
- **M5: real-call pass reported by the user on 2026-11-20** (app-next2 `687fca8`, crash fixed). Per-item check results weren't reported.
  - Core: `5ddfdc6`.
  - Platform (process loopback, session enumeration, recorder): `c6c76be`. Loopback activation works on the VM without audio devices.
  - Speakers and cloud route: `5c49c06`. Speaker separation uses pinned pyannote-seg-3.0 and WeSpeaker ONNX models in the speech helper; it worked on a 16 s Mac sample, and its 0.6 threshold is untuned.
  - The Meetings UI is in progress (UI worker).
  - Exit: a real 30-minute Teams or Zoom call, following `windows/INSTALL-TEST.md` "Meetings".
  - Note for the Mac app (not fixed; `main` is out of scope): the M5 worker found a latent race in Swift. A meeting can look finished before its last chunk's answer is applied, so speaker separation can start without that audio.
- **M6: local part done (`ce410d2`).**
  - Velopack 1.2.161 pinned. The Updater (GithubSource, `MURMUR_UPDATE_FEED` for local feeds), LoginItem (on at the first installed launch, as on the Mac), `windows/tools/package.ps1`, the `.github/workflows/windows-release.yml` draft (`workflow_dispatch`; sign and upload are off) and `windows/UPDATES.md`.
  - On the VM: install, delta update and uninstall kept user data byte-identical. The uninstall leftover Run value is fixed.
  - `RestartToUpdate()` was verified on the VM on 2026-11-20 (`e85e6d5`): 0.0.1 → 0.0.2 through the Settings button, data and Run value intact, with `-Phase restart` in test-update-flow.ps1. The delta was probably applied: the rebuilt full package differs from the feed's. Not run separately: the tray item.
  - Needs the user: the signing account, the vpk signing flags (unverified for 1.2.161), and publishing (manual, `windows-v*` tags, never latest).
- **UI workstream:** the Windows UI matches the Mac look (user direction 2026-11-19).
  - Pass 1, `2abe6ca`: shared window and sidebars, underline tabs, Mac History.
  - Pass 2, `de97c81`: switches, (?) icons, column width, Modes/Dictionary/Cues, welcome guide.
  - Pass 3, merged 2026-11-20 (merge of `windows-ui-meetings` plus a microphone-step fix). It adds the Meetings screen, sheets, call offer, Settings Meetings and Updates, Open at login, tray Restart to update, the voice edit shortcut row and dictionary suggestions. Harness 6/6 (median 318 ms). Known gaps: no meeting date filter, Lecture tab, speaker rename or live floating panel; no cloud meeting-transcription row; no "Not learned" list; the call offer isn't exercised live.
  - Pass 4, merged 2026-11-20: Home, Insights charts and streak calendar, settings search reveal, meetings date filter, speaker rename/merge/move, Not learned list. Harness 6/6 (median 314 ms). Remaining gaps: Lecture tab, live floating meeting panel, the Start Meeting Notes app picker, the settings search results overlay, Home info wording, and Insights app display names. Not yet viewed rendered: Insights below the fold, Dictionary, search reveal, filters.
  - Pass 5, merged 2026-11-20 (`749dcc9`): meeting app picker, live floating meeting panel with pill, Lecture tab, settings search overlay (Ctrl+K), Home row opens its History entry, app display names. Harness 6/6 (median 330 ms). Remaining: the panel isn't draggable and the Meeting indicator setting does nothing; search has no synonyms; Home info wording. Unviewed: search jump-to-result, Not learned, a real live meeting.
  - Merge check: CsWinRT1032 shows up in `dotnet build -c Release` (and CI) but not always in `dotnet publish`. Run the Release build on the VM before pushing.

## Active workers (21:40Z, 2026-11-20)

The earlier workers (32843b61, db434533, 1d14aebd) and this coordinator's session all closed at about 10:20Z; the cause is unknown. Their work was preserved, and Codex replacements resumed it at 21:40Z. All report to `b080c3f1`.

| Agent | Model | Worktree folder / branch | Assignment |
| --- | --- | --- | --- |
| `1fd6d415-f9a6-4cb4-81a3-e4fe7435fb6f` | Codex gpt-6.2-sol, medium | `windows-m3-live` / `windows-gpu` (a228fc5 pushed) | **GPU phase 2, finishing:** measured installer deltas (CI run 44716871878), cleanup CPU threads, fallback tests, re-running the harness, Mac checks. It holds the VM desktop. |
| `b8cf1636-b352-450d-82ef-858827f444b2` | Codex gpt-6.2-sol, medium | `windows-ui-fluent` / `windows-ui-parity` (5bd2519 local) | **UI parity:** cleanup gating, the pill waveform, the shimmer at release, status copy, the remaining outline audit, side-by-sides, the XamlParseException check. Next in line for the desktop. |
| `dd241070-a8df-41c0-b49a-94ea15761c61` | Codex gpt-6.2-sol, medium | `windows-m4-core` / `windows-local-notes` (work uncommitted at hand-over) | **Notes benchmark, finishing:** every local model failed the 30- and 60-minute runs at the 768-token cap. Diagnosing that, running the 1200-token check, finishing the long and low-end runs, then the recommendation. Stops before pinning. |

**When all three are merged:** stage the result on the PC as **app-next3**, in a new folder; never touch the running build. Send the switch steps to the orchestrator.

All other workers are archived. All three worktree workspaces are in use; create a new worktree from `windows` for any new task.

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

- **Fixed and merged (2026-11-20), and staged on the PC as app-next2 (`687fca8`):**
  - Start meeting crashed the app on the PC. The cause was the call-audio client being created on the UI thread (STA) and used on the MTA capture thread, so Drain threw E_NOINTERFACE. The fix creates and uses it on one MTA thread. The regression test `CallAudioStartedFromAnStaThreadKeepsCapturing` crashes the old code on the VM and passes on the fix. Background-thread crashes now go to CrashLog.
  - The spoken mode trigger ("email mode …") was never called on Windows. It's now applied after transcription, as in the Mac's Controller.deliver.
  - Harness: the `mode-email` case passes 5/5 checks. The full run passed 21/21 (median 289 ms). An earlier run passed 18/21 with three undiagnosed failures (Edge long-report no-speech, Code filler-think clipboard-restored, one more in Edge). Watch for a flake.
  - Waiting on the user to retest on the PC: the meeting crash and email mode.
- **Mac note (read-only):** "E-mail mode" (with a hyphen) is one token and won't match the trigger "email mode" (Dictionary.swift:27). The same applies on Windows. It's unfixed.

- **CI was red from `4aab0c6` to `1e316b0`.** The `windows-ci-fix` merge fixed it. The root cause was a real bug: `AudioCapture` and `AudioSessions` activated MMDeviceEnumerator in two different ways in one process, so whichever ran second threw InvalidCastException, both in the app and in the tests. The fix also added CsWinRT1032 casts and gates the learning keystroke test on CI.
- **PC app-next is staged at `0a21433`** (2026-11-20, about 05:05Z): `T:\Paseo Project\Murmur\app-next`. The previous build is `app-next.old-20261119-2100`. The user is running PC-CHECKLIST.md.

- **VM SSH drops (twice, about 02:00Z, 2026-11-20):** sshd resets at `ssh-userauth`. Both times it came right after Chrome probe runs, and it recovered on its own without a reboot. Afterwards: 4.6 GB disk and 6.8 GB RAM free, no chrome.exe left. Cause unknown. Avoid long browser-probe loops, and close browsers by PID.

- **The app freezes while downloading a model:** fix merged in `c8dd54f`. Progress is now sampled at 10 Hz with one pending UI update, and status and removal work run off the dispatcher. Verified on the VM with a synthetic transfer. Still to confirm: a real download on the user's PC.
- **Golden check "hang": not a bug.** A full `generate.sh --check` takes about 15 minutes, and longer under load; all 21 generators pass. Run it per generator name.
- **Notepad paste on the unattended PC:** passed 1 of 3 runs in the earlier smoke test. The user's own real-mic run in Notepad worked. Watch for it, but it's not reproduced with a person present.
- **Hotwords:** one correct made-up name was respelled in the spike (`Kestrelmoor`). Boosting is on when the dictionary has entries. The CTC rescoring alternative is still open.

## Needs user

- **GPU: the user chose option (a), both (about 10:05Z).** DirectML (a pinned sherpa-onnx native rebuild) and Vulkan (LLamaSharp.Backend.Vulkan.Windows) are bundled in every install. The budget is about +26 MB compressed / +73 MB installed; measure the real installer. "Use GPU when available" defaults ON, with silent CPU fallback and the reason logged (no content). CUDA is deferred. Keep the Live Words CPU fix and measure CPU % before and after it, and on GPU (VM now, PC later). The plan is `a4e89f4` on `windows-gpu`; phase 2 is handed to 32843b61.
- **Notes benchmark scope added:** a low-end CPU profile (the VM capped at about 4 threads with limited RAM). Per candidate: time to notes for 30- and 60-minute meetings, peak RAM and responsiveness. Then a hardware-aware default proposal (GPU vs CPU model, a low-RAM fallback, background low-priority generation, when to recommend Murmur Cloud). It goes to the orchestrator before pinning.
- **PC Live Words CPU baseline (app-next2, before the fix), run 2 (agent 4cc75ac7, about 10:25Z, user dictating):** Murmur.exe mean 4.7% / p95 17% of one core. MurmurSpeech.exe median 0%, with bursts in 7 of 119 s peaking at 1345% of one core (about 13.5 of the 24 threads). GPU about 0. One persistent MurmurSpeech PID; no per-capture process. The likely cause is an uncapped thread pool (ORT default). Handed to the GPU worker to find the path and cap it. The CSV is `T:\Paseo Project\Murmur\cache\livewords-cpu-baseline-2.csv`. Run 1 (f3da6743) caught idle only. PC agents can't `send_agent_prompt` to Mac agents; read their finish notification instead.
- **PC offline overnight (from 2026-11-20 about 09:45Z, user's call).** Parked until it's back: the GPU probe on its NVIDIA card (if unfinished), the GPU worker's PC measurements, and staging app-next3. The VM stays up.

- **M4 and M5 exits on the PC:** the user-run steps are in `windows/PC-CHECKLIST.md`.
  - the browser matrix: Chrome, Edge, Firefox, Brave;
  - learning on real fields;
  - voice edit against a real provider;
  - a 30-minute meeting following `INSTALL-TEST.md` "Meetings";
  - a real model download (the freeze fix).
  
  CI is green at `0d1e491`, and its artifact is `Murmur-windows-x64-unsigned`. Staging app-next on the PC waits for the PC host to come back online.
- **M6:** the signing account, the test-repo release runs and publishing. The setup steps are in `windows/UPDATES.md`, "Needs user". The pinned vpk 1.2.161 still needs signtool and the dlib; Velopack 1.3 or later signs directly.
- **Velopack 1.3:** the user asked for stable 1.3.x, but none exists. The only 1.3 build is the prerelease 1.3.39-g8d2844f; the latest stable is 1.2.161. Decided: stay on 1.2.161, and move to 1.3 when a stable release ships (UPDATES.md). The user may still override.
- **Optional:** a bigger VM disk, so parallel builds don't run low.

## Next actions

1. **app-next2 retest:** meeting PASS (2026-11-20). Email mode: pending, not reported. It covers the meeting crash (Start meeting, tray right-click, Stop) and Email mode (start the dictation with "Email mode, …"). The switch steps went to the orchestrator.
   - If the crash recurs: run a read-only PC agent for the Application event log (.NET Runtime 1026), `%TEMP%\Murmur-crash.txt` and `%LOCALAPPDATA%\CrashDumps`, as at 05:40Z. Never read History, transcripts or audio.
   - Earlier PC results on app-next (`0a21433`): learning PASS, model download PASS. Still to run: voice edit, the browser matrix (Firefox and Brave matter most), the 30-minute meeting.
2. **CI is green at `a186cfd`.** Before merging, run `dotnet build windows/Murmur.App.slnx -c Release` on the VM. It catches CsWinRT1032, which the Mac build and sometimes `publish` miss.
3. **Remaining gaps to schedule after the three active workstreams** (Codex 5h was 13% until 07:37Z; Claude weekly about 24%):
   - Firefox reader activation, if the matrix fails;
   - the hyphenated "E-mail mode" trigger (one token; both apps);
   - the RestartToUpdate tray item;
   - speaker threshold tuning on real calls;
   - a possible harness flake: 18/21 once (Edge no-speech, Code clipboard-restored), 21/21 on the rerun;
   - the pass 5 gaps: panel drag and the Meeting indicator setting, search synonyms, Home info wording.
4. **M6 with the user:** set up Azure Artifact Signing (UPDATES.md "Needs user"), then a test-repo release, then the first `windows-v*` draft. Velopack stays on 1.2.161 until a stable 1.3 ships.

**PC staging recipe:** `create_agent` on `srv_XXiOqPoelqaJ`, workspace `wks_3d3a18a1cff53984`, mode `auto`. Run `dotnet publish windows\src\Murmur.App -c Release -r win-x64 -o <new folder>` from `T:\Paseo Project\Murmur\src`, with the cache env vars on T:. Always use a new folder; never touch a running build. `archive_agent` can't reach PC agents.
