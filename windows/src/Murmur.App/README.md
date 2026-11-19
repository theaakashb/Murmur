# Murmur.App

The WinUI 3 tray app (unpackaged, self-contained). It builds on Windows only (the XAML compiler is Windows-only).

```
dotnet build windows/Murmur.App.slnx
windows\src\Murmur.App\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Murmur.exe
```

It starts in the tray with no window. Default dictation key: hold Right Ctrl. Paste the last dictation: Ctrl+Win+Shift+V. `--settings` and `--history` open that window at launch (development aid).

## Test hooks (see `windows/spec/test-hooks.md`)

| Variable | Effect |
| --- | --- |
| `MURMUR_DATA_DIR` | Data root instead of `%LOCALAPPDATA%\Murmur`. Credentials stay in memory, so Credential Manager is untouched. The single-instance lock is per data folder. |
| `MURMUR_SYNTHETIC_AUDIO` | Folder with `next.txt` naming a PCM16 or Float32 WAV; it is streamed in real time instead of the microphone. Also makes the keyboard hook accept injected keys (SendInput), which it ignores otherwise. |
| `MURMUR_TEST_EVENTS=1` | Content-free events in `<data dir>\diagnostics\events.jsonl`. |

Example (PowerShell, in the interactive session):

```powershell
$env:MURMUR_DATA_DIR = "C:\tmp\murmur-test"; $env:MURMUR_SYNTHETIC_AUDIO = "C:\tmp\synth"; $env:MURMUR_TEST_EVENTS = "1"
.\Murmur.exe
```

Put a `settings.json` in the data dir first to choose a route (for example `transcriptionRoute: "cloud"` with `customTranscriptionAPIBaseURL` and `customTranscriptionModel`).

## UI

The tray opens Settings and History. In Settings, the speech model is under **Dictation → Transcription**, and the cleanup model is under **Writing**. Manage cloud keys under **Providers**.

The app follows the Windows theme. For visual tests, set `MURMUR_THEME=light` or `MURMUR_THEME=dark` together with a throwaway `MURMUR_DATA_DIR`. The indicator reports recording and processing without taking focus. Only the copy card accepts clicks.

## Layout

- `App.xaml.cs` tray icon, menu, single instance, window opening.
- `AppServices.cs` data root, settings, credentials, History, dictionary, launch purges.
- `Controller.cs` the dictation controller; `ShortcutSpec.cs` the dictation key setting.
- `IDictationProcessor.cs`, `RulesOnlyProcessor.cs` the cleanup seam and its stand-in.
- `Recorders.cs` microphone and synthetic capture sources.
- `Bubble.cs` the no-activate status window; `SettingsWindow.cs`, `HistoryWindow.cs`.
- `CredentialAdapter.cs`, `TestEvents.cs`, `Native.cs`, `AppInfo.cs`.
