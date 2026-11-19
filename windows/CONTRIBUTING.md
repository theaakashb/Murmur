# Contributing to Murmur for Windows

Murmur for Windows is a C# port of the Mac app. The Mac app is the source of truth: a change is made and tested on the Mac first, then ported here. See [PLAN.md](PLAN.md) for the architecture and the repository's [CONTRIBUTING.md](../CONTRIBUTING.md) for the shared rules (privacy, commits, licensing).

## Requirements

- .NET SDK 10 (`dotnet --list-sdks`). `Murmur.Core` and its tests build on Windows, macOS and Linux.
- macOS with Swift, only to regenerate golden and shared files.

```sh
dotnet test windows/Murmur.Windows.slnx     # C# tests
windows/golden/generate.sh                  # macOS: regenerate golden and shared files from the Swift code
windows/golden/generate.sh --check          # macOS: fail if a committed golden or shared file is stale
python3 windows/tools/check_parity.py       # fail if a ported Swift file changed since its port
```

## What is written once, ported, or rewritten

| Kind | Examples | How it reaches Windows |
| --- | --- | --- |
| **Data, written once** | Corpora (`app/cleanup-corpus.json`, meeting notes corpora), golden outputs, word lists, prompts | Read in place, or exported from the Swift constants by `windows/shared/export_*.swift` into `windows/shared/*.json`, which `Murmur.Core` embeds. Never hand-copied into C#. |
| **Logic, ported** | Cleanup, guard, rules, dictionary, learning, routes, providers, meetings logic | C# file with the same name as the Swift file, same behavior, checked against golden files generated from the Swift code. |
| **UI and platform plumbing, rewritten** | Windows, bubble, tray, hotkeys, audio, insertion, credentials, updater | Written for Windows from the spec in `spec/`; checked by hand on a Windows PC. |

## Porting a Mac change

For every Mac PR that touches a ported file (listed in `porting.json`) or shared data:

1. On macOS, run `windows/golden/generate.sh`. If golden or shared files change, the C# tests now show what to port.
2. Port the logic change into the matching `.cs` file, and port any new or changed `check_*.swift` assertions into the matching test file.
3. `dotnet test windows/Murmur.Windows.slnx` passes.
4. `python3 windows/tools/check_parity.py --update` records the new Swift file hashes in `porting.json`, which says this port is done.
5. UI or platform changes: note them in the PR as "Windows follow-up needed" if not done in the same PR.
6. A Mac change to a file that isn't ported yet goes into the matching spec as a required behavior, with file:line refs and its check cases, so the milestone that ports it can't miss it.

### Check cases waiting for their milestone

These came from Mac fixes merged before their files were ported. Port each one with its file.

| Milestone | Swift check | Case | Spec |
| --- | --- | --- | --- |
| M2 | `check_app_services.swift:233-236` | restore never changes where dictations are sent | spec/settings.md |
| M2 | `check_app_services.swift:238-245` | restore drops links inside the backup (add a junction case) | spec/settings.md |
| M2 | `check_app_services.swift:246-250` | recording purge does not follow a linked folder (add a junction case) | spec/settings.md |
| M5 | `check_meetings.swift:307` | links, images and new lines are escaped (add U+0085/U+2028/U+2029) | PLAN.md "Meeting capture" |

CI fails when the C# output differs from the golden files, or when a ported Swift file changed without `porting.json` being updated. Stale golden or shared files fail `app/check.sh windows_parity` on the dev Mac (hosted macOS runners are older than the Mac that makes them).

## Code conventions

- Namespace `Murmur.Core`. One `.cs` file per Swift file, same base name. Start each file with `// Port of app/<File>.swift.`
- Swift `enum` used as a namespace becomes a `static class`; `struct` becomes a `record`/`record struct` or class as fits; string-backed enums become C# enums with the same raw strings in JSON. Members are PascalCase with the Swift name otherwise unchanged (`atPhrases` → `AtPhrases`).
- Swift `Character` is a grapheme cluster: where Swift counts or walks characters, use the helpers in `SwiftText.cs`, not `string.Length` or `char` loops. Swift `lowercased()` is `ToLowerInvariant()`.
- `NSRegularExpression` becomes `Regex` with `RegexOptions.CultureInvariant` and a match timeout; see PLAN "Regex and text differences".
- Constants that are data load from `windows/shared/*.json` (embedded resources). Generators sort sets and keys so output is deterministic.
- Tests: xUnit. `<Module>Tests.cs` ports `check_<module>.swift`; `<Module>GoldenTests.cs` compares `golden/data/<module>.json` case by case with `[Theory]` + `MemberData`, so each case reports on its own.
- No new NuGet packages without a reason recorded in PLAN.md and THIRD_PARTY_NOTICES.md.
- Same privacy rules as the Mac: never log transcript text, keys or response bodies; synthetic text only in tests and golden files.
