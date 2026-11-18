import Foundation

// Word diff and revert text; synthetic strings only.
@main struct WordDiffChecks {
    static func main() {
        func check(_ ok: Bool, _ what: String) { if !ok { print("FAIL \(what)"); exit(1) } }
        let r = WordDiff.runs(raw: "um so the meeting is at 3 pm", final: "So the meeting is at 3 PM.")
        check(r.contains(.init(kind: .removed, text: "um so")), "removed filler")
        check(r.contains(.init(kind: .same, text: "the meeting is at 3")), "kept words")
        check(WordDiff.changeCount(raw: "um so the meeting is at 3 pm", final: "So the meeting is at 3 PM.") == 1, "filler only")
        check(WordDiff.changeCount(raw: "same words", final: "same words") == 0, "identical")
        check(WordDiff.changeCount(raw: "hello world", final: "Hello, world") == 0, "case and punctuation are not changes")
        check(WordDiff.runs(raw: "", final: "").isEmpty, "empty")
        check(WordDiff.revertText(raw: "hi there", inserted: " Hi there.") == " hi there", "keeps leading space")
        check(WordDiff.revertText(raw: "hi there", inserted: "Hi there.") == "hi there", "no space added")
        print("PASS worddiff")

    }
}