# Updates (Windows)

Murmur for Windows installs and updates through [Velopack](https://velopack.io), pinned at **Velopack 1.2.161** (NuGet `Velopack` in `Murmur.App.csproj`) and **vpk 1.2.161** (`tools/package.ps1`). Keep the two equal. The design is in `PLAN.md` "Updates and releases".

## Where things live

- App: `%LOCALAPPDATA%\MurmurApp\current\`, with `Update.exe` beside it. An update replaces `current\`.
- User data: `%LOCALAPPDATA%\Murmur\`. Updates and uninstall never touch it; Delete All Data in Settings removes it.
- Start at login: an `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `Murmur` pointing at `Update.exe --processStart Murmur.exe`, which keeps working after each update. `LoginItem.Set(bool)` toggles it. It is switched on at the first installed launch (as on the Mac) and recorded in `openAtLoginDefaulted`. A build not installed by Velopack has no launcher, so `Set(true)` returns false.

## Client behaviour (`Updater.cs`)

- Checks at launch and every 4 hours while `updateChecksEnabled` is on; skips while dictating or recording a meeting.
- Downloads in the background (a delta when one applies). `Updater.UpdateReady` fires; `ReadyVersion` gives the version.
- `RestartToUpdate()` installs and restarts; quitting Murmur installs it silently without restarting.
- Source: `GithubSource` for `theaakashb/Murmur`, stable releases only. `MURMUR_UPDATE_FEED=<folder>` switches to a local folder feed for tests.
- A request carries nothing but the HTTP request for the public feed. Wording: `Updater.Explanation`.
- Settings/tray wiring (UI worker): bind to `App.Updater` (`ReadyVersion`, `RestartToUpdate()`, `UpdateReady`), `Updater.Explanation`, `LoginItem.IsOn` / `LoginItem.Set`.

## Building a release

`tools/package.ps1 -Version X.Y.Z -Out <folder>` publishes self-contained x64 and runs `vpk pack --packId MurmurApp`. Put the previous release's files in `<folder>` first and a delta is built. No signing yet: the marked spot in the script takes `--azureTrustedSignFile` or `--signParams`. `.github/workflows/windows-release.yml` is a manual draft with signing and upload off.

`tools/test-update-flow.ps1` checks install → delta update → uninstall against a folder feed and that seeded data in `%LOCALAPPDATA%\Murmur` is byte-identical afterwards.
