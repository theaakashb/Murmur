# Security

Murmur has access to microphone audio, meeting audio, focused fields, and local transcript history. Review the requested macOS permissions before enabling capture or correction learning. Download the app only from [murmurdictation.com](https://murmurdictation.com) or this repository's releases.

Please report suspected vulnerabilities privately through GitHub's **Report a vulnerability** control when available. If it is unavailable, contact the repository owner through an existing private channel to arrange disclosure. Do not put credentials, recordings, transcripts, or reproduction data from real users into public issues. Help → Report an Issue prefills an issue with content-free diagnostics only; vulnerabilities still go through the private route above. No private reporting channel is guaranteed to be configured yet.

## Local checks

Install [Gitleaks](https://github.com/gitleaks/gitleaks) (the CI version is 8.30.1), then run from the repository root:

```sh
gitleaks git . --log-opts='--all --full-history' --redact=100 --ignore-gitleaks-allow
gitleaks dir . --redact=100 --ignore-gitleaks-allow
git log --all --format=fuller | gitleaks stdin --redact=100 --ignore-gitleaks-allow
```

The history scan covers fetched refs only; a clean result does not cover unfetched branches, hosted comments, attachments, logs, releases, or private data formats the scanner does not recognize. CI uses a read-only token, does not persist checkout credentials, and uploads no scan report.

Before changing repository visibility, inventory remote branches, tags and pull-request refs, scan their full history, and review hosted content separately. Keep audit reports containing personal information or incident details outside the repository. A removed credential must be revoked or rotated; deleting its current file or rewriting history alone does not invalidate it.

## Data and development tools

Use synthetic data for fixtures and examples. Store provider keys through Settings in the macOS Keychain. Ignore rules are a safeguard against accidental staging, and do not remove data already committed.

The optional scripts under `app/tools/` include live provider benchmarks that can read configured Keychain credentials and send supplied text or audio to the selected provider. Read the script and its arguments before running it; the offline checks in CONTRIBUTING.md do not require provider keys.

The app is a desktop client, with no repository-hosted backend or account service. Custom servers are user-selected trust boundaries; plain HTTP outside loopback exposes audio or transcript content to the network path and is disclosed in Settings. Local history is protected with filesystem permissions, rather than application-level encryption.

## Windows

The Windows app (in development on the `windows` branch) keeps settings, History, the dictionary, downloaded models and retry audio in `%LOCALAPPDATA%\Murmur`, not the roaming profile, so transcripts do not sync. Uninstalling keeps this folder; **Delete All Data** in Settings removes History and meeting notes, the dictionary and learned entries, retry audio and saved API keys; settings and downloaded models stay. Provider keys are stored in Windows Credential Manager, encrypted per user, rather than in a file.

Windows builds are not code-signed yet; signing comes later. Until then Microsoft Defender or SmartScreen may warn about the installer, and some antivirus tools may flag the global keyboard hook and clipboard paste that dictation uses. Check that you got the build from this repository's releases.
