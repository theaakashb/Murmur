# CHECKPOINT: windows-pill-alpha (WIP, not built clean after the last edit; rebuild first)

Worktree T:\Paseo Project\Murmur\wt-pill2. Not staged. Env script: cache\envpill2.ps1; run scripts cache\run1.ps1 / runlive.ps1 (quote the folder arg: '"T:\...\scratch-alpha"').

## 1. Transparency (bisect so far)
- Live capture (`Murmur.exe --live-bubble "<folder>"`, BubbleLive.cs) shows the pill over a native magenta Win32 popup, captures the monitor with Windows.Graphics.Capture, crops in memory, writes only the crop.
- Variant switch: env MURMUR_PILL_VARIANT = comma flags: presenter (SetBorderAndTitleBar false,false), styles (strip frame styles + FRAMECHANGED), ncdisable (DWMWA_NCRENDERING_POLICY disabled), extend (DwmExtendFrameIntoClientArea -1), nolayered (no WS_EX_LAYERED; click-through via WM_NCHITTEST subclass), border. Default now "presenter,extend".
- Results (3:20-3:27): ncdisable (= c0e83b4 path) BLACK in this capture (border ring grey); presenter,styles (= 1c5f9ed) BLACK; every variant with `extend` TRANSPARENT: pill = only the 5 dots (non-magenta 690 px, near-black 0, ring 0), live-words/notice cards had transparent rounded corners (0 black). "presenter,extend" was clean; "extend" alone leaves the 2 px presenter border (grey, no black).
- PROBLEM: the capture could NOT show c0e83b4 as transparent (the brief's validity check), contradicting the user's report on app-next6.
- Since 3:33 every `extend` variant renders NOTHING (no dots, no card fill; XAML tree is visible, window visible) while ncdisable still draws. Reproduced even with Bubble.cs reverted to HEAD + only the flag lines, so it is not the chip/live-words edits. Suspects not yet ruled out: BubbleLive.cs changes (DisplayArea.FindAll / MURMUR_LIVE_DISPLAY pin / xaml diag line), Native.IsWindowVisible, or a changed DWM/machine state. The last step in progress was removing the FindAll lines from BubbleLive.cs (line 55-58) to test that; saved copies are in scratch-alpha\keep.
- Next: finish that bisect; re-run presenter,extend; if it draws again, it is the fix (verify the 5 states). Also try presenter + ClearBackdrop alternatives if not. Then report numbers for control (ncdisable), repro (presenter,styles), fix.

## 2. Live words timing
- Mac: app/Controller+Models.swift startLiveWords + ModelHelper.swift LiveStream (:232), helper ModelBench.swift stream (:104): FluidAudio streaming encoder 70_13_13 int8; one processBufferedAudio per fed buffer; partial emitted when text changes. Mac bubble shows the live card IMMEDIATELY with a dim "Listening…" (Bubble.swift:140-153, :330) and the last word dimmed.
- Windows: LocalTranscriber.StartLiveWords spawns a helper per recording (no prewarm), writer starts only after "ready", GPU attempt first (10 s startup limit before CPU fallback), partial dropped until the focus (password) check completes and not re-sent if unchanged; PLAN.md measured first partial 1.6-2.2 s. Model 560 ms chunks (Mac 70_13_13 is coarser).
- Done in code: Bubble.ShowLiveWords() (card up from start with dim "Listening…", settled/dim-last-word Runs via LiveWordsFacts.Split), called from Controller.AttachSpeech.
- Not done: no streaming model on disk (only the offline model in scratch-modes), so no synthetic-WAV before/after numbers. Ideas: re-send last partial once the focus check completes; start writing before ready; prewarm like Mac prepareLiveWords.

## 3. Mode indicator
- Finding: the Mac bubble has NO "mode name" indicator. Its equivalent is the changes chip (Bubble.swift:196-232 ModeRecoveryCard, :425-440 showRecovery, 8 s; Controller+Recovery.swift:52-66 offerModeRecovery): icon = slider if a writing mode shaped the text else sparkles, text "N change(s)", Undo (offer) / "Undone" / "Copy original", click opens History.
- Done: Bubble.ShowChanges(changes, usedMode, original) chip (8 s, Setting/Highlight icon, "N changes", "Copy original" button, click -> OpenHistory), Controller.OfferChanges(final, text) after a non-failed paste using WordDiff.ChangeCount(Cleanup.Input ?? RawText, text), App sets bubble.OpenHistory.
- Not ported: in-place Undo (revertToRaw/replaceInserted, Ctrl+Win+Z); chip not yet verified live or in a render.

## 4. Remaining
- `dotnet build windows/Murmur.App.slnx -c Release` (was 0 warnings before the last revert/restore), Core tests, clean bin/obj, build-server shutdown, check C:\Users\aakash\AppData\Local\NuGet\v3-cache and C:\Users\aakash\.dotnet do not exist (not yet checked).
- Remove dev-only env hooks if undesired (MURMUR_PILL_VARIANT, MURMUR_LIVE_DISPLAY, MURMUR_LIVE_HIDE).
