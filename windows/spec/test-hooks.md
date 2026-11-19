# Test hooks for the automated exit harness

The Windows app has three test-only hooks, used by `windows/harness/`. Each one is off unless its environment variable is set at launch, so a normal install never sees them. The Mac has the same idea in `LaunchOptions.swift` (synthetic fixtures with temporary storage).

| Variable | Effect |
| --- | --- |
| `MURMUR_DATA_DIR` | Replaces `%LOCALAPPDATA%\Murmur` as the data root. Settings, History, retry audio and diagnostics all live there, and the real data folder is never touched. |
| `MURMUR_SYNTHETIC_AUDIO` | A folder path. Replaces the microphone at recording start: the app reads `<folder>\next.txt`, which holds one absolute WAV path, and streams that WAV's samples in real time through the same conversion, level and retry-WAV path as the microphone. When the file runs out it sends silence until release. A missing `next.txt` or WAV fails the recording like "no microphone". |
| (with `MURMUR_SYNTHETIC_AUDIO`) | The keyboard hook also accepts injected key events. Otherwise it ignores them, so Murmur's own Ctrl+V never triggers anything. The harness needs this to drive Right Ctrl with `SendInput`. |
| `MURMUR_TEST_EVENTS` | `1` writes content-free events, one JSON object per line, to `<data dir>\diagnostics\events.jsonl`. |

**Events.** Each event has `t` (ISO time) and `event`, from this list:
- `ready`
- `hook-installed`
- `recording-started`
- `recording-stopped` (adds `seconds`)
- `transcribed`
- `cleaned` (adds `path`: the `CleanupPath` raw value)
- `paste` (adds `status`: verified, unverified or failed; plus `reason` when it failed, and `historyId`)
- `clipboard-restored`
- `error` (adds `stage` and `reason`)

The data is content-free: no transcript text, no audio, no keys.

**What the harness does with these.** It reads the final text from `History` in the data dir (synthetic text only). It also writes its own `settings.json` into the data dir before launch: a Custom Provider on `http://127.0.0.1:<port>/v1` for transcription and cleanup. That's a loopback route, so no key is needed.

With `MURMUR_DATA_DIR` set, credentials are kept in memory, so Credential Manager is never touched. Harness run folders and screenshots stay on the test machine and are never committed: the desktop can show personal windows.
