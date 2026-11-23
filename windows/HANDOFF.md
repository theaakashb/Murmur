# Murmur for Windows: coordinator handoff

**Current coordinator: Coordinator 8, `8100de45-febc-45d5-af28-a4e5e0fc4a2f` (Claude Opus 5.5 on the PC, Murmur workspace `wks_c227e9c5312b0db4`; it took over from C7 587400c2 on 2026-11-23).** Orchestrator: Mac agent `fd8c6433-7045-4091-a1df-9cc55df6334f` on `srv_c2AxDOwFn9yz` (it replaced 8f691026). PC→Mac `send_agent_prompt` works. Only Claude runs on the PC. Hand off at about 70% context.

**Every process sets** TEMP, TMP, DOTNET_CLI_HOME, NUGET_PACKAGES, NUGET_SCRATCH, NUGET_HTTP_CACHE_PATH and NUGET_PLUGINS_CACHE_PATH under `T:\Paseo Project\Murmur\cache\` (temp, dotnet, nuget\packages, nuget\scratch, nuget\http, nuget\plugins), **including `dotnet build-server shutdown`**. W2 once leaked `C:\Users\aakash\.dotnet` from a Bash call without DOTNET_CLI_HOME, so prefer PowerShell with the full env. Workers check at the end that `C:\Users\aakash\AppData\Local\NuGet\v3-cache` and `C:\Users\aakash\.dotnet` are absent. SDK: `T:\Paseo Project\Murmur\tools\dotnet\dotnet.exe`. Build `windows\Murmur.App.slnx -c Release` yourself before merging: a worker once reported a clean build that had 2 App errors.

**check_parity on the PC:** the CRLF checkout gives false CHANGED. Run it on an LF export: `git -c core.autocrlf=false archive HEAD app module-one windows`, extract under cache\temp, `py -3 -I -B windows/tools/check_parity.py [--update]`, copy `windows/porting.json` back.

**Staging recipe (PC):** `dotnet publish windows\src\Murmur.App -c Release -r win-x64 -o <new app-nextN> -p:MurmurSherpaDirectML=<dir>`, where `<dir>` holds DirectML.dll, onnxruntime.dll, onnxruntime_providers_shared.dll and sherpa-onnx-c-api.dll copied from `app-next11\Speech` (same hashes as app-next8). Check that `Cleanup\runtimes\win-x64\native\vulkan`, `Speech\DirectML.dll` and `Speech\silero_vad.onnx` exist. Never launch staged builds on the PC.

**State (2026-11-22, windows = 3369ce6 + this note, parity 88/88, PC Release build clean, Core tests 22775/22775; Mac goldens regenerated at 3369ce6 and generate.sh --check passes):**
- **PR #101 and #102 are ported** (d1091a5, 358d0f3): the DictationLanguages policy, the cloud hint, the manual-only dictionary for non-English, non-English session gating, the language picker (Settings Transcription and Welcome), per-model download rows, the #101 onboarding copy, the Home "Open Dictionary" row, and the showInDock default. `shared/settings.json` was hand-edited (showInDock mac:true, plus W1's `dictationLanguages` row); the Mac regen confirms or overwrites it.
- **Multilingual models (user-approved, pinned in windows-models.json):**
  - Parakeet TDT v3 int8 `csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8@ed5d7456`: 670.6 MB, CC BY 4.0.
  - Whisper turbo int8 `csukuangfj/sherpa-onnx-whisper-turbo@2ca6ff69`: 1.04 GB, MIT upstream; the HF repo has no tag.
  - `silero_vad.onnx` (sha256 9e2449e1…, MIT) is bundled in Murmur.Speech.
  - Both multilingual models run on CPU only (orchestrator decision; `LocalSpeechModels.MayUseGpu`, test `MultilingualModelsNeverRequestTheGpu`). TDT is faster on CPU, and sherpa-onnx access-violates creating Whisper on DirectML. The helper ignores `--gpu` for `--parakeet-tdt` and `--whisper-turbo`. Clips up to 30 s decode whole. Longer ones split at Silero-chosen cuts into pieces of at most 28 s, and a small repeated fragment at a cut is possible.
  - PC bench (test_wavs only): TDT CPU RTF about 0.04–0.05 (DML is slower); Whisper CPU RTF about 0.3.
  - Downloaded copies for VM side-loading: `T:\Paseo Project\Murmur\models\multilingual\{parakeet-tdt-v3,whisper-large-v3-turbo}`.
- **History rewrite (user request, 2026-11-23):** the 362 windows-only commits (origin/main..windows) were rewritten with git filter-branch to drop Claude Co-Authored-By lines and set the committer; main's commits were not touched, so main merges still work. origin/windows was force-pushed (dd05c1b → 5c18931). **Do not add Co-Authored-By or other attribution lines to commits or PRs from now on.** Any older checkout of windows must reset to origin/windows. The local safety branch `backup/windows-pre-rewrite` was deleted on 2026-11-23 after the user approved app-next14.
- **2026-11-23 ~02:00 local, PC shut down by the user.** The user runs the INSTALLED app-next15 (%LOCALAPPDATA%\MurmurApp) and approved the beta. Beta: tag windows-v0.1.0-beta.1 on 1ed34a5 (7a3df90 + YAML fix; merged as ba4f8f6), run https://github.com/theaakashb/Murmur/actions/runs/45150815435 in progress at shutdown; result and draft link still to report to the user. For app-next16 (unstaged, on windows at 8ff1d5c): a55bc21 VM fixes, a2fd2ee first-run opens Home, 8ff1d5c WorkflowYamlTests. VM checks for app-next16: Home/Insights at 560 px and wide, tip dots at launch, Esc mid-transcription deletes the WAV, fresh Setup.exe install opens Home. Build app-next16 with a version above 0.1.0-beta.1 (e.g. 0.1.0-beta.2.dev16).
- **app-next15** (7a3df90, version 0.1.0-alpha.15) is staged as `app-next15` (folder) and `app-next15-installer` (Velopack Setup.exe 218.4 MB, sha256 a7899cf8...). It waits for the VM run: install from Setup.exe, every Settings pane and Welcome step (the Back arrow), then uninstall. Then the user tests the installer; if they verify it, the orchestrator tags `windows-v0.1.0-beta.1` and the workflow makes a DRAFT pre-release (7a3df90 accepts pre-release versions and passes vpk --pre). Pack recipe: `windows\tools\package.ps1 -Version V -Out <dir> -SherpaDirectML <dir with the 4 DML DLLs>` with the T: env plus DOTNET_ROOT and PATH set to tools\dotnet.
- **The user runs app-next14** (switched 2026-11-23; VM green on f974203; user: meeting notes works, app-next14 good). Roll back to app-next12. ecd910f (Welcome Back mojibake + wider MojibakeTests) is unstaged; batch it with the rest of the VM findings and have the VM open the Welcome steps. The user will report if the freeze recurs. Keep every app-next* folder unless the orchestrator says otherwise. C7 is archived.
- **app-next14** (f974203) is the current build for the user and the VM checker (48023806). app-next13 made Settings fail to open: W6 put flag Images in a TextBlock via InlineUIContainer, which WinUI 3 rejects (ArgumentException in InlineCollection.Add). The fix uses a RichTextBlock. Lesson: Core tests and a clean build miss XAML runtime errors, so every VM check must open every Settings pane and the Welcome steps. The user runs builds from `T:\Paseo Project\Murmur\app-nextN\Murmur.exe`; the crash log is `%TEMP%\Murmur-crash.txt` (exception types and stacks only, safe to read).
- **User feedback on app-next12/13 (2026-11-23):** the indicator, transcription, Learning, the Gemma download and the tip card are good. Fixed so far: mojibake (ad91b5c), Show tips with the presenter port, the Ctrl+K tooltip, the Insights grid, picker flags, and the Settings crash. Open: the 82 s freeze is unreproduced; ask the user to note the time of the next one and leave it in retry, then read the crash log and event log for that time. The user's "meetings note" remark is unclear (asked; no answer yet).
- **app-next13** (5c18931) is staged for the VM run: the encoding repair, Show tips presenter, hidden Ctrl+K tooltip, adaptive Insights grid, picker flags, and the freeze work. The 82 s English freeze is NOT reproduced (the English helper does a synthetic 82 s clip in 6-19 s here); fixed what was proven: Stop no longer waits out a stuck helper on the UI thread, focus/mode waits are bounded at 3 s, and local transcription has a deadline of 120 s + 2x the clip that keeps the audio for retry.
- **Known issue (not fixed):** on clips over 30 s, Whisper can repeat a small fragment at a cut point (W2 saw one on a 40 s test clip). Pieces are at most 28 s, cut where Silero finds a pause, with no overlap handling. A fix would de-duplicate the words at the seam or cut only at longer pauses.
- **PR #100 notes merge is DONE** (52263a0): origin/main 969168b, then windows-notes-contract 2eb78bf. The `wt-notescore` worktree is removed (branch kept).
- **Builds:**
  - app-next12 (f052069: the CPU-only multilingual policy plus "Show in taskbar") is staged for the orchestrator's VM run. app-next11 (b21d8be) is the previous build. app-next10 is deleted.
  - app-next9 (d1091a5) is older.
  - app-next8 is confirmed by the user, and app-next7 is the fallback. app-next6 is deleted.
  - The old `app` folder waits on the user. Never delete the folder the user runs (check the Murmur.exe path).
- **Pending:**
  - The VM real-app run of app-next12 (plus the Show in taskbar toggle): the picker, TDT/Whisper downloads and dictation, Welcome, and the Home Dictionary row.
- **Workers:** none live. W1 67ee7e44, W2 f829b4dc and W3 9b32e188 are archived. Their worktrees are removed; the branches windows-langcore, windows-langspeech and windows-langui are kept.
- **Lessons:**
  - Offscreen XAML captures never show real composition. The pill is a ULW layered window, and the orchestrator captures it on the VM.
  - Static-init order bugs only show in a fresh process (see StaticInitOrderTests).
  - AppResourceKeysTests catches deleted App.xaml keys.

**Older notes below are history.** Earlier notes bench: f6d92e6 on windows-local-notes; no 60-minute run completed before the fix. Old workers 76b8269f, 67fb94a5, fa8785fe and 8aa2528c are archived.

**PC test rule:** don't run the Murmur.Platform tests that drive Notepad or SendInput on the PC; it is the user's live desktop. Core tests only.

**PC checkout (user request, 2026-11-20 about 23:50Z):** Windows work now uses `T:\Paseo Project\Murmur\Murmur` (a full clone of `windows`), registered as Paseo workspace `wks_c227e9c5312b0db4` (project `prj_45f0c70bbdc987ad`) on `srv_XXiOqPoelqaJ`. New worktrees branch from it, as `T:\Paseo Project\Murmur\wt-<task>`. `T:\Paseo Project\Murmur\src` stays only until the in-flight `wt-fix2` worktree (worker 8aa2528c) is merged; then retire it. Never delete the `app*` folders.

**PC agent rule (user, 2026-11-20 about 23:50Z):** on the PC, every Windows agent runs in the **Murmur** project's workspace `wks_c227e9c5312b0db4` (`T:\Paseo Project\Murmur\Murmur`) or a `wt-*` worktree under `T:\Paseo Project\Murmur\`. **Never use C:\ as an agent cwd or workspace.** PC project `prj_45f0c70bbdc987ad` is named "Murmur" so it groups with the Mac's Murmur project in the sidebar. The daemon config (`~/.paseo/config.json`) has no projects-folder setting.

Refreshed 2026-11-20 about 09:50Z by coordinator 3 (`268d6a36-00e7-40c3-a2bd-0b2dacfa5718`; it took over from `f9e7f4a7` at 04:00Z). The orchestrator is `8f691026-6ec3-41d4-8c14-4cba2ef6db44`. Read this first, then `windows/PLAN.md` (the Status section is current), `windows/CONTRIBUTING.md` and `~/.claude/CLAUDE.md`.

## RELOCATION (2026-11-21 about 00:10Z): the Mac goes offline

The user's Mac (coordinator 4 `b080c3f1`, the orchestrator and every Mac worker) is going off the network. Work moves to the **Star Labs PC** (Paseo `srv_XXiOqPoelqaJ`, workspace `wks_3d3a18a1cff53984`, Claude only; Codex is disabled there).

- **New coordinator: Coordinator 5, `6d103697-2809-4c04-9ee1-2f8981dd2eaa` (Claude Opus 5.5 on the PC).** The Mac workers 9811836d and abc068c2 are archived after their pushed checkpoints (`windows-ui-fix2` a66aace, `windows-local-notes` 0be3a5d). Key pill finding: the Mac minimal pill is 80×40 pt with no background, just five blue dots (app/Bubble.swift:32-39, :336). Bubble.cs has no DWM calls and clips with SetWindowRgn. It owns everything below, works from this file, and keeps every PC rule: T: only; TEMP, TMP, DOTNET_CLI_HOME, NUGET_PACKAGES and NUGET_SCRATCH on T:; no user data; never touch the running app; nothing left on C:.
- **The Windows UI fix** (branch `windows-ui-fix2`; checkpoint pushed by Mac worker 9811836d) moves to a PC Claude worker, which builds in `T:\Paseo Project\Murmur\src` and stages **app-next5**. Scope: the pill's light outline (find the real layer: DWM border colour DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE, corner preference, backdrop, XAML Border, shadow); the pill size scaled to Mac px (DPI-aware; Mac constants in app/Bubble*.swift); the lightning glyph outside the capsule (match the Mac, else remove); provider fields gated per route exactly as on the Mac (see below). Verify with crops at 100% and 150%, which the PC can do only without disturbing the user's running app, so prefer an offscreen render of the pill.
  - Mac gating (code-read on main 2239a4f): transcription Local shows a help line only; Cloud shows URL, model and key; Murmur Cloud shows its key only (SettingsPaneViews.swift:150-175). Cleanup custom fields show only when the route is Custom API AND the style isn't Faithful (:254-280). The meeting notes and meeting transcription pickers have no provider fields. Voice edit has no picker. The API Keys pane lists all keys (by design).
- **Notes benchmark:** branch `windows-local-notes` (checkpoint pushed by Mac worker abc068c2). The Mac Metal runs are PAUSED until the Mac is back. PC agent `fa8785fe` (Qwen CPU/Vulkan, then the T: cleanup and C: audit) continues on the PC; poll it with get_agent_activity. No pinning; the model choice goes to the user.
- **Paused until the Mac is back:** the Mac Metal notes runs, the Mac-side .NET build, tests and `check_parity`, golden generation, and Mac UI snapshots. The VM (`murmur-win`) is reachable only by SSH from the Mac, so its use pauses as well.
- **Agents VM** (`srv_eTAT9llxMw-3`, Linux): it has git 2.34, gh 2.102 (signed in as a different GitHub account, Penguin0011), and the claude and codex CLIs, but NO dotnet. Not used for now. It would need a .NET 10 SDK installed before it could run Core tests.
- **Merging:** the PC coordinator may merge worker branches into `windows` after the checks it can run (a PC Release build, tests, a fresh publish containing `Cleanup\runtimes\win-x64\native\vulkan`). Record in the merge message that Mac `check_parity` was skipped.

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

- **Git:** push only to `windows`. No main, no PRs, no merges to main. Exception, user-authorized 2026-11-20: the Mac custom-provider-fields fix may push `fix/custom-provider-fields-route-gating` and open a PR to main; it is never merged by agents.
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
- **Cache environment:** set `DOTNET_CLI_HOME`, `NUGET_PACKAGES`, `NUGET_SCRATCH`, `TEMP` and `TMP` to `T:` paths in EVERY process, including `dotnet build-server shutdown`, so nothing lands on C:. The user requires nothing left on C:. The 2026-11-20 GPU bench left `C:\Users\aakash\.dotnet` (3 sentinel files), `%TEMP%\NuGetScratch` and three temp entries. Cleanup is pending.
- **PC launch rule:** don't launch staged builds while the user's Murmur runs (single-instance). Verify staged builds by their files and hashes.

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
| (GPU, done) | | `windows-gpu` | **Merged as `b59e1cf`; 1fd6d415 archived.** DirectML (rebuilt sherpa with ORT DML 1.24.4) and Vulkan cleanup are bundled. Setup +26.30 MB, installed +72.67 MB. GPU defaults ON with silent CPU fallback; cleanup is capped at min(8, physical cores). PC steps are parked in PLAN.md. |
| (UI parity, done) | | `windows-ui-parity` | **Merged as `b2823ad`; b8cf1636 archived.** Covers: route gating for cloud-only cleanup options (Core plus tests), the five-bar RMS waveform, a 200 ms tail, a single processing shimmer, friendly voice-edit engine names, and keyboard-only focus outlines. VM harness 6/6, no XamlParseException in 6 fresh startups. Median release-to-paste was 679 ms vs 303 ms baseline on a shared VM with uncontrolled load; recheck it on the PC. |
| (PC GPU bench, done) | | `bench-gpu\results` kept | **Results are in PLAN.md "Star Labs PC results".** Batch on CPU (DirectML was slower); Live Words and cleanup on GPU (fix `1617150`). app-next3 (`d3b9fe8`, batch on DirectML) is staged but superseded. |
| (app-next4, accepted) | | `T:\Paseo Project\Murmur\app-next4` | **The user is running app-next4 and called it an improvement (2026-11-20, about 23:50Z).** app-next3 is being deleted (PC agent 4906e362). Open feedback is handed to 9811836d. |
| `9811836d-aac3-4a33-9b35-a70bfd2620b8` | Claude Sonnet 5.5 (Codex low until about 02:32Z) | `windows-ui-fluent` / `windows-ui-fix2` | **Feedback on app-next4:** the pill still has a light outline (find the real layer: DWM border colour, backdrop and the rest) and is too big (scale to Mac px, DPI-aware); identify the lightning glyph outside the capsule; Custom Provider fields must show only when that route is selected, on every route picker. Crops at 100% and 150%. Then stage app-next5. |
| `c5b19e23-fe3b-4a2d-9f57-98753ff62579` | Claude Sonnet 5.5 | `mac-provider-gating` / `fix/custom-provider-fields-route-gating` (off origin/main) | **Mac check (user-authorized):** are custom-provider fields shown for non-custom routes? If yes, the smallest fix, the full check.sh, build.sh and check_ui.sh --snapshot, then push and a PR to main, NOT merged. If no, file:line evidence only. |
| `e1a87324-9b6c-46f8-aed9-fcbf40b90b0e` (PC) | Claude Sonnet | PC | Cleanup: the C: bench leftovers (lists .dotnet first) and src bin/obj. |
| (Vulkan publish, done) | | `windows-vulkan-publish` | **Merged as `c4ddc78`.** Root cause: the nested helper restore reused stale `.nuget.g.props` imports from MSBuild's XML cache after an upgrade from a pre-Vulkan restore. The fix sets `MSBuildIsRestoring=true` on that Restore call only, and publish now fails if `ggml-vulkan.dll` is missing. On the VM: the upgrade repro passes, 3/3 clean publishes are complete, the helper inventory equals a2e76f8 (427 files, DirectML fixtures copied), and the guard fires. |
| `abc068c2-60b3-4608-b994-fb7d551d1ae5` | Claude Sonnet 5.5 (Codex quota at 10%, 23:22Z) | `windows-m4-core` / `windows-local-notes` (`d27e4b4`) | **Notes benchmark, finishing:** Mac Metal Gemma-1200 60m and bounded runs, plus PC agent fa8785fe (Qwen CPU/Vulkan, then cleanup and C: audit). VM and CPU reruns were skipped (CPU-only is already a no). Interim: no CPU default; Gemma 3 4B as the GPU pilot. |
| `fa8785fe` (PC, created by dd241070) | Claude Sonnet | `T:\Paseo Project\Murmur\bench-notes` | PC notes benchmark: 5900X CPU and RTX 3080 Vulkan. |

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

**DirectML on staged builds:** a plain `dotnet publish` ships the stock CPU sherpa. To get DirectML, pass `-p:MurmurSherpaDirectML=<dir>` with the native runtime: the CI artifact `sherpa-directml-win-x64` from run 44716871878 (kept 14 days, until about 2026-12-04), or build it with `windows/tools/build-sherpa-directml.ps1`, which needs VS and CMake. `windows/tools/package.ps1` builds it itself and fails if DirectML or Vulkan is missing.

**PC staging recipe:** `create_agent` on `srv_XXiOqPoelqaJ`, workspace `wks_3d3a18a1cff53984`, mode `auto`. Run `dotnet publish windows\src\Murmur.App -c Release -r win-x64 -o <new folder>` from `T:\Paseo Project\Murmur\src`, with the cache env vars on T:. Always use a new folder; never touch a running build. `archive_agent` can't reach PC agents.
