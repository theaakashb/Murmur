import Foundation

// Word diff and revert text; synthetic strings only.
@main struct WordDiffChecks {
    static func main() {
        func check(_ ok: Bool, _ what: String) { if !ok { print("FAIL \(what)"); exit(1) } }
        let r = WordDiff.runs(raw: "um so the meeting is at 3 pm", final: "So the meeting is at 3 PM.")
        check(r.contains(.init(kind: .removed, text: "um")), "removed filler")
        check(r.contains(.init(kind: .same, text: "the meeting is at 3")) && r.contains(.init(kind: .tweaked, text: "PM.", marks: [0, 1, 2])), "kept words")
        check(WordDiff.changeCount(raw: "um so the meeting is at 3 pm", final: "So the meeting is at 3 PM.") == 1, "filler only")
        check(WordDiff.changeCount(raw: "same words", final: "same words") == 0, "identical")
        check(WordDiff.changeCount(raw: "hello world", final: "Hello, world") == 0, "case and punctuation are not changes")
        check(WordDiff.runs(raw: "", final: "").isEmpty, "empty")
        let p = WordDiff.runs(raw: "see the doc", final: "see the doc.")
        check(p == [.init(kind: .same, text: "see the"), .init(kind: .tweaked, text: "doc.", marks: [3])], "added period is one tweaked word")
        check(WordDiff.runs(raw: "ok knock it", final: "ok Knock it") == [.init(kind: .same, text: "ok"), .init(kind: .tweaked, text: "Knock", marks: [0]), .init(kind: .same, text: "it")], "case change marks the letter")
        check(WordDiff.runs(raw: "hello, there", final: "hello there")[0] == .init(kind: .tweaked, text: "hello", marks: []), "dropped punctuation marks nothing")
        check(WordDiff.runs(raw: "on tuesday we", final: "on Tuesday. We") == [.init(kind: .same, text: "on"), .init(kind: .tweaked, text: "Tuesday.", marks: [0, 7]), .init(kind: .tweaked, text: "We", marks: [0])], "case plus period marks only those characters")
        check(WordDiff.runs(raw: "um so the", final: "So the") == [.init(kind: .removed, text: "um"), .init(kind: .tweaked, text: "So", marks: [0]), .init(kind: .same, text: "the")], "a deleted word next to a case change pairs the right words")
        let real = WordDiff.runs(raw: "see teh doc", final: "see the doc.")
        check(real.contains(.init(kind: .removed, text: "teh")) && real.contains(.init(kind: .added, text: "the")), "real word changes stay replacements")
        check(WordDiff.revertText(raw: "hi there", inserted: " Hi there.") == " hi there", "keeps leading space")
        check(WordDiff.revertText(raw: "hi there", inserted: "Hi there.") == "hi there", "no space added")
        print("PASS worddiff")

    }
}