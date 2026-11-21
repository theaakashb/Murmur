# CHECKPOINT: windows-pill-alpha (WIP, not built clean after the last edit; rebuild first)

Worktree T:\Paseo Project\Murmur\wt-pill2. Not staged. Env script: cache\envpill2.ps1; run scripts cache\run1.ps1 / runlive.ps1 (quote the folder arg: '"T:\...\scratch-alpha"').

## 1. Transparency: SOLVED by not using XAML for the pill (branch windows-pill-layered)
- The pill and its cards are drawn by `Murmur.Platform/PillRenderer.cs` (GDI+, antialiased, 32bpp premultiplied alpha) into a DIB, and shown by `Murmur.Platform/PillWindow.cs`: a plain Win32 popup, WS_EX_LAYERED, `UpdateLayeredWindow` with ULW_ALPHA / AC_SRC_ALPHA. No WinUI window, no DWM frame, no border, no backdrop: what is composed is exactly the bitmap, so alpha-0 pixels are really transparent. `Bubble.cs` keeps the Controller's API and only decides what to show and where.
- Hit-testing: WS_EX_TRANSPARENT unless a card with buttons is up; then WM_NCHITTEST answers HTCLIENT on the rounded shape and HTTRANSPARENT elsewhere, WM_MOUSEACTIVATE returns MA_NOACTIVATE.
- Tests: `PillRendererTests` (Murmur.Platform.Tests) render every state at 100/150/200%, light and dark, and assert alpha, antialiasing, interior colour, premultiplication and hit rectangles.
- Dev demo: `Murmur.exe --bubble-demo <listening|processing|label|live-start|live-words|notice|error|card|changes|all> <seconds> [light|dark]`.
- Removed: the DwmExtendFrameIntoClientArea / ncdisable / presenter variants, ClearBackdrop, BubbleLive (Windows.Graphics.Capture), BubbleRender (RenderTargetBitmap), MURMUR_PILL_VARIANT and the other env hooks.

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
- Live check on the VM over a coloured background (orchestrator); then stage app-next8.
- Not done: the changes chip's in-place Undo; streaming-model timing numbers.
