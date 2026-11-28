# Install and test on a real Windows PC

Murmur for Windows is free, offline, private dictation. This is for the M2 and M3 exits on a PC with a real microphone. The automated harness (`harness/README.md`) already covers the pipeline on a VM; this run covers what it can't: real microphones, a real provider, a person's keyboard, Teams and Slack, and speed on real hardware.

Never paste dictated text into issues, chats or the results file. Describe what went wrong instead.

## Requirements

- Windows 11 22H2 or later, x64.
- A working microphone. Windows Settings > Privacy & security > Microphone must allow desktop apps.
- Internet access, for the speech model download (663 MB) and the cloud route.
- Notepad, Microsoft Edge, VS Code, and Teams or Slack signed in.
- About 3 GB of free disk space.
- For part A only: a Custom Provider address and key.

## 1. Get the build

The build is unsigned until M6. Either:
- Download the `Murmur-windows-x64-unsigned` artifact from the latest green "Windows port" run on the `windows` branch: `gh run download --repo theaakashb/Murmur -n Murmur-windows-x64-unsigned`.
- Or let the coordinator copy it over SSH.

It's a folder of about 360 MB with `Murmur.exe`, `Speech\MurmurSpeech.exe` and `Cleanup\Murmur.Cleanup.exe`.

## 2. Install

1. Put the folder at `%LOCALAPPDATA%\Programs\Murmur-dev\`, so `Murmur.exe` is directly inside it. No admin rights are needed.
2. Start `Murmur.exe`. Because it's unsigned, Windows SmartScreen shows "Windows protected your PC": choose More info > Run anyway.
3. Murmur starts in the tray as a blue "M" icon. Right-click it for Settings, History and Quit.
4. Your data lives in `%LOCALAPPDATA%\Murmur\`: settings, History and the dictionary. Removing the program folder doesn't touch it. Settings > Delete All Data does.
5. The real installer, updates, Start menu entry and start at login come in M6. This dev build has none of them.

## 3. Models

- **Speech model:** Settings > Models > Parakeet Unified English 0.6B > Download. The license appears before anything downloads. The app verifies every file against its pinned checksum.
- **Cleanup model:** Settings > Models > Vedu Scribe 0.8B > Download (812 MB, Apache-2.0). It's pinned to `clouddevaakash/vedu-scribe-0.8b-gguf` at revision `edf27597`, and the app verifies its SHA-256 (`6859e09e…`).
- "Local (this PC)" and "Local model (this PC)" are enabled only once their models are installed and verified.

## Part A: 20 dictations, cloud route (M2 exit)

1. In Settings:
   - Transcription: Custom Provider. Cleanup: the same.
   - Enter the address, model and key, then Save and Validate.
   - Keep the shortcut on Right Ctrl. If your keyboard has no Right Ctrl, which is common on laptops with a Copilot key, pick another key with Record key.
2. Copy `harness/real-mic/results-template.csv` somewhere and fill in one row per dictation.
3. For each row:
   1. Copy some text of your own first, so you can check the clipboard comes back.
   2. Click into the target app's text field. Hold Right Ctrl, say a sentence or two of anything, and release.
   3. Check that:
      - the text appeared where the cursor was;
      - Ctrl+V afterwards pastes your copied text, not the dictation;
      - Win+V doesn't list the dictation (clipboard history must be on: Settings > System > Clipboard);
      - History (tray menu) shows the entry;
      - your first word wasn't cut off.
4. The 20 rows: 5 Notepad, 5 Edge text box, 4 VS Code, 4 Teams or Slack message box, 2 another app (Word, Outlook or Discord).
5. Also try once:
   - Escape while recording, which should discard it;
   - a dictation into a password field, which should be refused;
   - Ctrl+Win+Shift+V, which should paste your last dictation again.

**Passes when:** every row has text where the cursor was, the clipboard came back and the dictation is absent from Win+V. Note anything slow or odd.

## Part B: local models, real speech (M3 exit)

1. Quit Murmur from the tray. Start it with `harness\real-mic\start-bench.cmd` instead. If Murmur isn't in the default folder, pass the path to `Murmur.exe` as an argument. That uses a separate data folder (`%LOCALAPPDATA%\Murmur-bench`), so your own History stays clean, and keeps each recording for the comparison.
2. In that instance's Settings:
   - choose Local (this PC) for transcription and Local model (this PC) for cleanup;
   - download both models in that instance's Settings > Models, or copy their folders from `%LOCALAPPDATA%\Murmur\Models\` into `%LOCALAPPDATA%\Murmur-bench\Models\`.
3. In Notepad, dictate the five scripts in `harness/real-mic/scripts.tsv` in order (short-1, short-2, medium-1, medium-2, long-1). Read each one in a single hold, at a normal pace:
   - short ones: about 5 seconds;
   - medium ones: about 30 seconds;
   - long-1: about 2 minutes. Murmur stops at 5 minutes.
   
   If one goes wrong, delete `%LOCALAPPDATA%\Murmur-bench` and start the five again.
4. Run `powershell -NoProfile -ExecutionPolicy Bypass -File harness\real-mic\collect.ps1`. It writes `%LOCALAPPDATA%\Murmur-bench\export\`:
   - the five WAVs;
   - `manifest.json`, the scripts' text as the reference;
   - `timings.json`, with clip, transcription and cleanup seconds, measured on this PC.
   
   It copies no History text.
5. **Your OK needed:** to compare against the Mac on identical audio, the export folder, which contains your voice reading the scripts, is copied to the Mac. There it is run through:
   - the Mac app's own Parakeet (Core ML);
   - the Windows engine (`bench/AsrBench`).
   
   Then it's scored with `bench/score.py` and the results go into `MODEL_BENCHMARK.md`, numbers only. Delete the WAVs afterwards.

**Passes when:** all five scripts paste correctly with no key and no network, and the WER is close to the Mac's on the same audio. Also record the speed on this PC.

## Meetings: a real 30-minute call (M5 exit)

Needs a PC with a speaker or headset and a microphone, and a Teams or Zoom call with at least one other person who knows it is being recorded. Nothing here is verified on the agents VM except that the capture path starts without crashing.

**Before:**
- Windows build 20348 or later (`winver`; Windows 11 22H2 qualifies). Murmur falls back to microphone only on anything older.
- Settings: a Custom Provider address, model and key for notes, or leave notes on None. Local speech model downloaded.
- Run the automated device test once: `set MURMUR_REAL_AUDIO=1` then `dotnet test windows\tests\Murmur.Platform.Tests --filter MeetingCaptureTests`. It plays a tone and checks it is heard and that the sample count follows the wall clock through 3 s of silence.

**Run:**
1. Join the call. Tray menu > **Start meeting** (records the microphone plus everything except Murmur). Accept the consent box the first time. For a single app instead, launch Murmur with `--start-meeting` only for the all-apps case; a per-app meeting starts from the call offer once the UI worker connects it.
2. Talk for 30 minutes with the other person, including some silent stretches of 30 s or more and a stretch where only they speak.
3. Switch headset or output device once mid-call, and mute/unmute the call app once.
4. At about minute 15, kill Murmur from Task Manager (End task on `Murmur.exe`). Start it again within a minute. Then continue the call and record 5 more minutes.
5. Tray > **Stop meeting**. When asked, choose the notes route.

**Check:**
- Me and Them lines alternate in the order they were spoken. A line spoken by the other person in a quiet period starts at about its real time (compare to a stopwatch or the call's own clock), not early.
- Teams (new, WebView2) and Zoom: the other person's words are in Them. A call in a browser tab: Them is the whole browser tree, so other tabs' audio is also captured (expected).
- After the kill: the saved meeting exists and is finished, and the chunks left behind are transcribed on the next launch (lines for the minutes before the kill are present, with a gap near the kill).
- The mid-call device switch leaves at most a short gap (the log line `meeting-gap` is content-free); the stream resumes without restarting Murmur.
- Notes: written on the chosen route, each point cites lines, and the transcript is saved either way.
- Nothing is left in `%LOCALAPPDATA%\Murmur\retry-audio\meetings\` for the finished meeting.
- Record: whether the Teams audio came from `msedgewebview2.exe` children (tree capture), and whether the call-app microphone use produced a call offer (`diagnostics\events.jsonl` with `MURMUR_TEST_EVENTS=1`).

**Passes when:** aligned Me/Them for the full 30 minutes, recovery after the kill, and notes (or the transcript alone) saved.

**Speaker separation and the cloud route (also on the PC):**
- Download both speaker models (Models: segmentation, embedding) and turn on "Separate speakers after a meeting" (stored as `meetingSpeakers`; the Settings row is the UI worker's). Run a call with two or more other people. After Stop, lines should be labelled Speaker 1, Speaker 2, and the kept call audio (`retry-audio\speakers\<id>.wav`) disappears when separation finishes. Note how many speakers it finds against how many really spoke; the clustering threshold (stored 0.6, sherpa cosine distance) is a starting value from one 16 s two-speaker sample, not tuned.
- Kill Murmur right after Stop while "Separating speakers" shows: on the next launch the separation resumes from the kept audio (or fails and the audio is deleted after 24 hours).
- Cloud: set `meetingTranscription` to cloud with a Custom Provider transcriber configured. Chunks go to the provider; a failed chunk is transcribed here and marked. With the cloud route, speaker separation still splits the whole lines by speaker but without word times.
- Quit during a meeting: a confirmation appears; Yes stops and saves it.

## Uninstall

- Quit Murmur from the tray.
- Delete `%LOCALAPPDATA%\Programs\Murmur-dev\`.
- To remove your data too, delete `%LOCALAPPDATA%\Murmur\` and `%LOCALAPPDATA%\Murmur-bench\`, and the `Murmur/*` entries in Credential Manager (or use Settings > Delete All Data first).
