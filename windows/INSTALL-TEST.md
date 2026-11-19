# Install and test on a real Windows PC

This is for the M2 and M3 exits on a PC with a real microphone. The automated harness (`harness/README.md`) already covers the pipeline on a VM; this run covers what it can't: real microphones, a real provider, a person's keyboard, Teams and Slack, and speed on real hardware.

Never paste dictated text into issues, chats or the results file. Describe what went wrong instead.

## Requirements

- Windows 11 22H2 or later, x64.
- A working microphone. Windows Settings > Privacy & security > Microphone must allow desktop apps.
- Internet access, for the speech model download (663 MB) and the cloud route.
- Notepad, Microsoft Edge, VS Code, and Teams or Slack signed in.
- About 3 GB of free disk space.
- For part A only: a Murmur Cloud key, or a Custom Provider address and key.

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
- **Cleanup model:** the row shows "Not published yet", because the cleanup model isn't on Hugging Face until you approve the upload. Until then, side-load the staged file:
  - Copy `vedu-scribe-0.8b-Q8_0.gguf` (811,843,296 bytes, SHA-256 `6859e09e41502d2c1665b9462b2f9f37c7e485ebddd14185813f22a89554c708`) into `%LOCALAPPDATA%\Murmur\Models\vedu-scribe-0.8b-0000000\`.
  - Restart Murmur. The row then reads "Downloaded", and the app verifies the file against the pinned checksum.
  - The coordinator can copy it over SSH. The file is in `~/murmur-m3/hf-stage/vedu-scribe-0.8b-gguf/` on the Mac.
- "Local (this PC)" and "Local model (this PC)" are enabled only once their models are installed and verified.

## Part A: 20 dictations, cloud route (M2 exit)

1. In Settings:
   - Transcription: Murmur Cloud (or Custom Provider). Cleanup: the same.
   - Enter the key, then Save and Validate.
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
   - for the cleanup model, side-load it into `%LOCALAPPDATA%\Murmur-bench\Models\vedu-scribe-0.8b-0000000\` as in step 3;
   - download the speech model again or copy the folder from `%LOCALAPPDATA%\Murmur\Models\`.
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

## Uninstall

- Quit Murmur from the tray.
- Delete `%LOCALAPPDATA%\Programs\Murmur-dev\`.
- To remove your data too, delete `%LOCALAPPDATA%\Murmur\` and `%LOCALAPPDATA%\Murmur-bench\`, and the `Murmur/*` entries in Credential Manager (or use Settings > Delete All Data first).
