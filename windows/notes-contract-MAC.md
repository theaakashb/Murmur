# Notes contract change: the Mac PR (branch off origin/main)

Approved by the user for both apps (2026-11-21). Windows has it implemented and tested in `windows/src/Murmur.Core` (commit "Notes contract: item caps…", branch `windows-notes-contract`). This file is the exact Swift change that makes the Mac match, so the regenerated `windows/shared` and `windows/golden/data` files come out **byte-identical** to what Windows already carries. Line numbers are origin/main at `windows@9e3c5d0`'s base (`app/` is not touched by the Windows branch). The Swift below is not compiled (no Swift on the PC): treat it as the spec and build it on the Mac.

## The contract

1. **Item caps in the prompt.** Every extraction and merge system prompt ends with one more line after the rules, exactly:
   `Return at most 8 POINT, 10 DECISION and 12 ACTION lines. Prefer the most important supported items.`
   (Six prompts change: `extract`, `extractRoom`, `extractRoomSpeakers`, `extractSpeakers`, `merge`, `mergeSpeakers`. `lectureRules` is unchanged and is still appended after it.)
2. **Truncation after parsing.** A parsed reply keeps its first 8 points, 10 decisions and 12 actions. Applied: (a) to every extraction reply, before grounding; (b) to every merge reply; (c) to each part again after grounding; (d) to the final draft after its grounding. Title and summary are never cut.
3. **Split on output exhaustion.** A reply that ends at the reply-token cap (`finish_reason == "length"`) is a new provider error, `ProviderError.outputTruncated(ProviderID)`. In the notes writer it takes the **same path as `NotesFailure.tooLarge`**: split that extraction chunk (more than one line) or merge batch (more than two parts) in half once, retry each half, and if a half also fails, fail with the existing "Part of this meeting is too long…" / "This meeting is too long…" reasons. A half is never split again.
4. **A lone draft passes through a merge.** When a merge batch is split and a half holds one draft, that draft is used as it is (no request).
5. **Merge batching is unchanged**: the existing character-budget rule (`route.chunkCharacters`), no count limit.
6. Not Mac-relevant: the Windows on-device route (Gemma 3 4B) passes a 5,000-character budget and a 1,200-token reply cap; Windows takes the budget as a parameter.

## Swift changes

### app/ProviderTypes.swift
- **:107** after `case malformedResponse(ProviderID)` add
  ```swift
  /// The reply stopped at the output-token cap (`finish_reason` "length"): the answer is cut off. Meeting notes split the request once and retry; everything else treats it like `malformedResponse`.
  case outputTruncated(ProviderID)
  ```
- **:131** after `case .malformedResponse: return "malformedResponse"` add `case .outputTruncated: return "outputTruncated"`.
- **:144** `.malformedResponse(let p), .contentBlocked(let p),` → `.malformedResponse(let p), .outputTruncated(let p), .contentBlocked(let p),`
- **:154** `.server, .malformedResponse, .serverNotRunning: return .preserveRetryLater` → `.server, .malformedResponse, .outputTruncated, .serverNotRunning: return .preserveRetryLater`
- **:185** `case .malformedResponse:` → `case .malformedResponse, .outputTruncated:` (same text: "<name> returned a response Murmur couldn't use.")
- Any other exhaustive `switch` on `ProviderError` the compiler flags: treat `.outputTruncated` like `.malformedResponse`.

### app/OpenAICompatible.swift
- **:158-162**
  ```swift
  switch first["finish_reason"] as? String {
  case nil, "stop": break
  case "content_filter": throw ProviderError.contentBlocked(provider)
  case "length": throw ProviderError.outputTruncated(provider)      // new
  default: throw ProviderError.malformedResponse(provider)
  }
  ```
  and the doc comment at :151 `A \`length\` finish (truncated) is an error.` → `… is \`outputTruncated\`.`

### app/Controller+Meetings.swift
- **:114** `catch ProviderError.payloadTooLarge { throw NotesFailure.tooLarge }` → `catch ProviderError.payloadTooLarge, ProviderError.outputTruncated { throw NotesFailure.tooLarge }`
- Update the doc comment on `NotesFailure` (app/MeetingNotes.swift:527): "`tooLarge` is the route's context limit or a reply that stopped at the output cap; either splits the request and retries once…".

### app/MeetingNotes.swift
- **before :82** (`enum MeetingNotesPrompt {`) add
  ```swift
  /// How many items of each kind one reply keeps: a long meeting's drafts stay small enough to merge.
  enum MeetingNotesLimits {
      static let maxPoints = 8, maxDecisions = 10, maxActions = 12
      static var instruction: String {
          "Return at most \(maxPoints) POINT, \(maxDecisions) DECISION and \(maxActions) ACTION lines. Prefer the most important supported items."
      }
  }
  ```
- **:133-134** (end of `rules`; the closing `"""` is :134)
  ```swift
          - Leave a list empty rather than inventing items.
          """
  ```
  →
  ```swift
          - Leave a list empty rather than inventing items.
          \(MeetingNotesLimits.instruction)
          """
  ```
  (This produces `…inventing items.\nReturn at most 8 POINT…` with no trailing newline. `rules` feeds both `extract` and `merge`, and the structured (Apple) route too.)
- **`struct Draft` (:179-)** add
  ```swift
  /// The first `MeetingNotesLimits` items of each kind.
  func capped() -> Draft {
      var copy = self
      copy.points = Array(points.prefix(MeetingNotesLimits.maxPoints))
      copy.decisions = Array(decisions.prefix(MeetingNotesLimits.maxDecisions))
      copy.actions = Array(actions.prefix(MeetingNotesLimits.maxActions))
      return copy
  }
  ```
- **:563** `parts = parts.map { ground($0, in: whole, reasons: &reasons, lecture: lecture) }` → `parts = parts.map { ground($0, in: whole, reasons: &reasons, lecture: lecture).capped() }`
- **:565** `draft = ground(draft, in: whole, reasons: &reasons, lecture: lecture)` → `draft = ground(draft, in: whole, reasons: &reasons, lecture: lecture).capped()`
- **:593** (extract) `return [draft]` → `return [draft.capped()]`
- **:624/:627** (merge) at the top add `if batch.count == 1 { return batch }` (before `let prompt = …`), and `return [draft]` → `return [draft.capped()]`
- No change to `reduce` or `chunks`: the batching stays as it is.

### windows/golden/golden_meeting_notes_corpus.swift
The corpus fixtures reply with a point for every line plus the traps; with the caps the traps would be cut before grounding and the file would stop testing grounding. Make the generator (and the C# test, already done) call grounding directly. Replace the `let outcome = try await MeetingNotesWriter.write(…)` line (:25) and the `result[entry.id]` block (:26-30) with
```swift
let numbered = MeetingNotesPrompt.number(meeting)
let whole = NotesGrounding.source(numbered, myNotes: meeting.myNotes)
var reasons: [NotesGrounding.Reason: Int] = [:]
let grounded = MeetingNotesWriter.ground(Draft.parse(draft.text)!, in: whole, reasons: &reasons)
result[entry.id] = [
    "points": grounded.points.map(\.lines), "decisions": grounded.decisions.map(\.lines),
    "actions": grounded.actions.map(\.lines), "dropped": reasons.values.reduce(0, +),
    "reasons": Dictionary(uniqueKeysWithValues: reasons.map { ($0.key.rawValue, $0.value) })
]
```
`golden/data/meeting_notes_corpus.json` must regenerate **unchanged**.

### app/check_providers.swift
- **:280** `expectError(.malformedResponse(.customAPI), "truncated output")` → `expectError(.outputTruncated(.customAPI), "truncated output")`
- **:523** `expectError(.malformedResponse(.localServer), "local truncated output")` → `expectError(.outputTruncated(.localServer), "local truncated output")`
- **:311** extend: `&& ProviderError.outputTruncated(.customAPI).disposition == .preserveRetryLater`.

### app/check_meetings.swift: new cases (after the overflow cases, ~:473). Windows twins: `windows/tests/Murmur.Core.Tests/MeetingNotesContractTests.cs`
Use `long2` (the two-chunk Apple meeting) or a one-chunk meeting; `Generate` closures as in the neighbours.
1. **Prompt line:** `MeetingNotesPrompt.extract(…, nonce: "n").system.hasSuffix("- Leave a list empty rather than inventing items.\n" + MeetingNotesLimits.instruction)`, the same for `merge([…], nonce: "n")` and with speakers; and `write(… lecture: true)` sends `…instruction + "\n" + lectureRules` last.
2. **Caps on extraction:** a one-chunk reply with 12 POINT, 14 DECISION and 15 ACTION grounded lines gives at most 8/10/12 notes items.
3. **Caps on merge:** `long2` where the merge replies 20 POINT lines → the notes hold at most 8 key points.
4. **`Draft.capped()`:** 20 items of each kind → 8/10/12, first ones kept, title and summary kept.
5. **Split on exhaustion:** first extraction call throws `ProviderError.outputTruncated(.customAPI)` through the `generate` wrapper's mapping (use `NotesFailure.tooLarge` directly in the closure, as the existing overflow case does) → 3 extraction calls for a 4-line chunk, halves `[L1,L2]` and `[L3,L4]`, then one merge.
6. **One retry then failure:** every call throws → exactly 2 calls (whole, first half) and `.failed("Part of this meeting is too long for the notes route.")`; a one-line chunk → 1 call, same failure.
7. **Merge exhaustion:** a batch of 3 drafts that throws → split 1 + 2, the lone draft is not sent (assert no merge request has one part); every merge throwing → `.failed("This meeting is too long for the notes route.")`.
8. **Grounding still applied:** within the caps an ungrounded POINT (invented date) and a DECISION citing an unknown line are dropped and counted in `dropped`/`reasons`.
9. **Long input:** a ~60-minute meeting (800 short lines, `route.chunkCharacters` forced to 5,000 via a test-only parameter or a `.appleIntelligence` route, which is already 5,000) with a fake engine that throws `tooLarge` for one extraction and for the first merge over 60 POINT lines completes, makes `chunks + 2` extraction calls, caps the notes at 8 points, and no merge prompt exceeds 2 × 5,000 characters.

## Shared and golden files the Mac regeneration changes (all hand-edited on Windows to the expected result; the regen must produce no further diff)

| File | Change |
|---|---|
| `windows/shared/meeting_notes.json` | six `prompt.*` strings end `…inventing items.\nReturn at most 8 POINT, 10 DECISION and 12 ACTION lines. Prefer the most important supported items.` (`export_meeting_notes.swift` itself is unchanged) |
| `windows/golden/data/meetings.json` | the one `prompt.system` string, same added line |
| `windows/golden/data/providers.json` | the two `"name":"length"` chat cases (`customAPI`, `localServer`): `"kind":"malformedResponse"` → `"kind":"outputTruncated"`; message and disposition are unchanged |
| `windows/golden/golden_meeting_notes_corpus.swift` | generator change above; `windows/golden/data/meeting_notes_corpus.json` regenerates **unchanged** |

Everything else (`routes.json`, `cleanup_prompt.json`, the model golden files) is unaffected. After regenerating, run `windows/golden/generate.sh` and `git diff --stat windows/shared windows/golden`: only the three data files above may differ from this branch, and they must not differ from what is already here. Any other diff is a bug in one of the two sides.

## Not in this PR
The local-notes model pin, the Settings › Models row, the no-GPU warning and the on-device notes engine are Windows only.
