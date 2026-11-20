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

`-Phase restart` (run in the logged-in console session, after `install`) drives the real `RestartToUpdate()`: it launches the installed app with `--settings`, waits for the `update-ready` test event, selects Updates and invokes "Restart to Update" through UI Automation (no input simulation), then checks the new version, that the old process exited, that the untouched seed files are byte-identical and that the `Run` value survived. Verified on the VM (0.0.1 → 0.0.2, 2026-11-20). Delta evidence: the downloaded `packages\MurmurApp-0.0.2-full.nupkg` differs in size and hash from the feed's full package, so it was rebuilt from the 329 KB delta (Velopack wrote no log file to confirm this directly).

## Needs user: signing and publishing (M6)

Nothing below has been done. Agents hold no Azure access and no keys.

**Azure Artifact Signing setup (the user, in the Azure portal):**
1. An Azure subscription. For an individual developer, the billing account type must be "Individual", and its legal name and address must match a government ID. Individuals must be in the US or Canada; organizations have a wider country list.
2. Register the resource provider `Microsoft.CodeSigning` (Subscription > Resource providers).
3. Create an Artifact Signing account. The SKU is `Basic` (about USD 10 a month, per Velopack's docs). Note the region endpoint, such as `https://eus.codesigning.azure.net`.
4. Identity validation (portal only; it takes 1 to 30 business days): Organization or Individual, Public Trust. This needs the "Artifact Signing Identity Verifier" role.
5. Create a certificate profile of type Public Trust.
6. For CI: create an app registration with a GitHub OIDC federated credential for this repo, and give it the "Artifact Signing Certificate Profile Signer" role on the profile. Add the repo secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`. These are IDs, not passwords.
7. Send the coordinator the endpoint, the account name and the profile name. They go in a non-secret `metadata.json` (`Endpoint`, `CodeSigningAccountName`, `CertificateProfileName`).

**Then the agents:**
- Velopack stays on stable 1.2.161 (decided 2026-11-20). No stable 1.3 exists yet; the only 1.3 build is the prerelease 1.3.39-g8d2844f. With 1.2.161, `--azureTrustedSignFile` signs through `signtool.exe` plus the Artifact Signing dlib, on a Windows runner only. Install both in CI before `vpk pack`, and verify the flags against `vpk pack -h` on 1.2.161.
- When a stable 1.3 ships, move to it. Bump NuGet and vpk together, then rerun `tools/test-update-flow.ps1` on the VM. From 1.3, vpk signs directly through DefaultAzureCredential, without signtool or the dlib.
- Wire `azure/login` with OIDC and the flag into `tools/package.ps1` and `windows-release.yml`, then confirm `signtool verify /pa` on `Setup.exe` and `Murmur.exe`.

**User approvals still needed:** a test-repo release run; the first `windows-v*` draft release; and publishing. Publishing is manual and the release is never marked latest.
