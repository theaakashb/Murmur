// deps: CleanupTypes CleanupText CleanupGuard RuleCleaner CleanupPrompt PrivateFiles Dictionary Learning Retention
import Foundation

// Exports the prompt texts of CleanupPrompt and TransformPrompt. Most of them are built inside functions, so this
// recovers them by building prompts with marker values (private-use characters and a fixed nonce) and cutting the
// output into templates. Placeholders are {name}; the C# port fills each template in one pass. Every template is
// checked here by rebuilding the Swift output from it, so a wording change exports cleanly or fails loudly.

let id = "QZXNONCE", m1 = "\u{E001}", m2 = "\u{E002}", m3 = "\u{E003}", m4 = "\u{E004}"

func fail(_ what: String) -> Never { FileHandle.standardError.write(Data("export_cleanup_prompt: \(what)\n".utf8)); exit(1) }
func expect(_ ok: Bool, _ what: String) { if !ok { fail(what) } }
func after(_ s: String, _ prefix: String) -> String { expect(s.hasPrefix(prefix), "tail does not extend the base prompt"); return String(s.dropFirst(prefix.count)) }
func commonPrefix(_ xs: [String]) -> String { var p = xs[0]; for x in xs.dropFirst() { p = String(zip(p, x).prefix { $0 == $1 }.map(\.0)) }; return p }
func commonSuffix(_ xs: [String]) -> String { String(commonPrefix(xs.map { String($0.reversed()) }).reversed()) }
/// The text between `a` and `b` in `s`.
func between(_ s: String, _ a: String, _ b: String) -> String {
    guard let x = s.range(of: a), let y = s.range(of: b, range: x.upperBound..<s.endIndex) else { fail("marker missing") }
    return String(s[x.upperBound..<y.lowerBound])
}
func placeholders(_ s: String) -> String { s.replacingOccurrences(of: id, with: "{id}") }
func fill(_ t: String, _ values: [String: String]) -> String {
    var out = t
    for (k, v) in values { out = out.replacingOccurrences(of: "{\(k)}", with: v) } // Markers only; never "{" in a value here.
    return out
}

struct RewriteLines: Encodable { var fillers: String, corrections: String, punctuation: String, lists: String, grammar: String }
struct Rewrite: Encodable { var frame: String, noOps: String, opsHeader: String, standard: RewriteLines, professional: RewriteLines }
struct Strict: Encodable { var frame: String, spoken: String, noOps: String, opsHeader: String, fillers: String, corrections: String, punctuation: String, lists: String }
struct Spellings: Encodable { var polish: String, strict: String, separator: String }
struct ContextUser: Encodable { var open: String, close: String, separator: String, selected: String, clipboard: String, window: String, names: String }
struct Cleanup: Encodable {
    var styleNotesTail: String, contextTail: String, spellingsTail: String
    var rewrite: Rewrite, strict: Strict, spellings: Spellings, notes: String, contextSystem: String, user: String, context: ContextUser
}
struct Transform: Encodable {
    var previousAIPromptBaseline: String, aiPromptDetailRule: String, previousDetailAIPromptBaseline: String, aiPromptConditionRule: String
    var previousConditionalAIPromptBaseline: String, previousEmailBaseline: String, previousNotesBaseline: String
    var previousStructuredAIPromptBaseline: String, previousStructuredEmailBaseline: String, previousStructuredNotesBaseline: String
    var baseline: [String: String], frame: String, purpose: [String: String], terms: String, termsSeparator: String, notes: String, user: String
}
struct Export: Encodable { var cleanup: Cleanup; var transform: Transform }

func system(_ o: CleanupOptions, terms: [String] = [], context: ScreenContext? = nil, instructions: String = "") -> String {
    CleanupPrompt.build(transcript: "x", options: o, dictionaryTerms: terms, nonce: id, context: context, instructions: instructions).system
}
/// The operations paragraph of a base prompt and the frame around it ("{ops}").
func split(_ s: String) -> (frame: String, ops: String) {
    var paragraphs = s.components(separatedBy: "\n\n")
    expect(paragraphs.count > 2, "unexpected prompt layout")
    let ops = paragraphs[1]; paragraphs[1] = "{ops}"
    return (paragraphs.joined(separator: "\n\n"), ops)
}
func single(_ op: String, polish: Bool, professional: Bool, spoken: Bool = false) -> CleanupOptions {
    CleanupOptions(fillers: op == "fillers", corrections: op == "corrections", lists: op == "lists", punctuation: op == "punctuation",
                   polish: polish, professional: professional, spoken: spoken)
}
func lines(_ ops: String) -> [String] { ops.components(separatedBy: "\n") }

@main struct Gen { static func main() throws {
    // Rewrite (Polish and Professional) system prompts.
    let none = CleanupOptions(fillers: false, corrections: false, lists: false)
    let rewriteBase = split(system(CleanupOptions(fillers: false, corrections: false, lists: false, polish: true)))
    func rewriteLines(_ pro: Bool) -> (header: String, lines: RewriteLines) {
        var got: [String: String] = [:], header = "", grammar = ""
        for op in ["fillers", "corrections", "punctuation", "lists"] {
            let s = split(system(single(op, polish: true, professional: pro)))
            expect(s.frame == rewriteBase.frame, "rewrite frame differs by operation")
            let l = lines(s.ops); expect(l.count == 3, "rewrite operations paragraph")
            header = l[0]; got[op] = l[1]; grammar = l[2]
        }
        return (header, RewriteLines(fillers: got["fillers"]!, corrections: got["corrections"]!, punctuation: got["punctuation"]!, lists: got["lists"]!, grammar: grammar))
    }
    let standard = rewriteLines(false), professional = rewriteLines(true)
    let rewrite = Rewrite(frame: rewriteBase.frame, noOps: rewriteBase.ops, opsHeader: standard.header, standard: standard.lines, professional: professional.lines)

    // Strict system prompts; spoken adds a phrase to one rule.
    let strictBase = split(system(none)), strictSpoken = split(system(CleanupOptions(fillers: false, corrections: false, lists: false, spoken: true)))
    expect(strictBase.ops == strictSpoken.ops, "strict no-ops paragraph")
    let pre = commonPrefix([strictBase.frame, strictSpoken.frame]), suf = commonSuffix([String(strictBase.frame.dropFirst(pre.count)), String(strictSpoken.frame.dropFirst(pre.count))])
    expect(strictBase.frame.count == pre.count + suf.count, "spoken only inserts text")
    let spokenText = String(strictSpoken.frame.dropFirst(pre.count).dropLast(suf.count))
    var strictLines: [String: String] = [:], strictHeader = ""
    for op in ["fillers", "corrections", "punctuation", "lists"] {
        let s = split(system(single(op, polish: false, professional: false)))
        expect(s.frame == strictBase.frame, "strict frame differs by operation")
        let l = lines(s.ops); expect(l.count == 2, "strict operations paragraph")
        strictHeader = l[0]; strictLines[op] = l[1]
    }
    let strict = Strict(frame: pre + "{spoken}" + suf, spoken: spokenText, noOps: strictBase.ops, opsHeader: strictHeader,
                        fillers: strictLines["fillers"]!, corrections: strictLines["corrections"]!, punctuation: strictLines["punctuation"]!, lists: strictLines["lists"]!)

    // Tails: dictionary terms, style notes, screen context.
    let polishOn = CleanupOptions(polish: true)
    func spellings(_ o: CleanupOptions) -> (String, String) {
        let tail = after(system(o, terms: [m1, m2]), system(o))
        let sep = between(tail, m1, m2)
        return (tail.replacingOccurrences(of: m1 + sep + m2, with: "{terms}"), sep)
    }
    let (polishTerms, sep1) = spellings(polishOn), (strictTerms, sep2) = spellings(.standard)
    expect(sep1 == sep2, "term separator")
    let notes = placeholders(after(system(.standard, instructions: m3), system(.standard))).replacingOccurrences(of: m3, with: "{notes}")
    let context = ScreenContext(selected: m1, clipboard: m2, window: m3)
    let contextSystem = after(system(.standard, context: context), system(.standard))
    expect(!contextSystem.contains(m1) && !contextSystem.contains(id), "context system text is constant")

    // User message: the transcript block, and the screen context block before it.
    func user(_ c: ScreenContext?) -> String { CleanupPrompt.build(transcript: m4, options: none, nonce: id, context: c).user }
    let plain = user(nil)
    func wrapped(_ c: ScreenContext) -> (String, String) {
        let u = user(c); guard let r = u.range(of: plain) else { fail("context user layout") }
        return (String(u[..<r.lowerBound]), String(u[r.upperBound...]))
    }
    let (one, names) = wrapped(ScreenContext(selected: m1, clipboard: "", window: ""))
    let (two, _) = wrapped(ScreenContext(selected: "", clipboard: m2, window: ""))
    let (three, _) = wrapped(ScreenContext(selected: "", clipboard: "", window: m3))
    let heads = [one, two, three].enumerated().map { String($0.element.components(separatedBy: [m1, m2, m3][$0.offset])[0]) }
    let tails = [one, two, three].enumerated().map { String($0.element.components(separatedBy: [m1, m2, m3][$0.offset])[1]) }
    let open = commonPrefix(heads)
    expect(Set(tails).count == 1, "context block close")
    let labels = heads.map { String($0.dropFirst(open.count)) }
    let (all, _) = wrapped(context)
    let mid = between(all, m1, m2)
    expect(mid.hasSuffix(labels[1]), "context separator")
    let contextUser = ContextUser(open: placeholders(open), close: placeholders(tails[0]), separator: String(mid.dropLast(labels[1].count)),
                                  selected: labels[0] + "{text}", clipboard: labels[1] + "{text}", window: labels[2] + "{text}", names: placeholders(names))

    let cleanup = Cleanup(styleNotesTail: CleanupPrompt.styleNotesTail, contextTail: CleanupPrompt.contextTail, spellingsTail: CleanupPrompt.spellingsTail,
                          rewrite: rewrite, strict: strict, spellings: Spellings(polish: polishTerms, strict: strictTerms, separator: sep1),
                          notes: notes, contextSystem: contextSystem, user: placeholders(plain).replacingOccurrences(of: m4, with: "{sent}"), context: contextUser)

    // Self-check: rebuild every combination of the operations from the templates.
    for bits in 0..<128 {
        let o = CleanupOptions(fillers: bits & 1 != 0, corrections: bits & 2 != 0, lists: bits & 4 != 0, punctuation: bits & 8 != 0,
                               polish: bits & 16 != 0, professional: bits & 32 != 0, spoken: bits & 64 != 0)
        var rebuilt: String
        if o.polish {
            let l = o.professional ? rewrite.professional : rewrite.standard
            var ops: [String] = []
            if o.fillers { ops.append(l.fillers) }
            if o.corrections { ops.insert(l.corrections, at: o.professional ? ops.count : 0) }
            if o.punctuation { ops.append(l.punctuation) }
            if o.lists { ops.append(l.lists) }
            if !ops.isEmpty { ops.append(l.grammar) }
            rebuilt = fill(rewrite.frame, ["ops": ops.isEmpty ? rewrite.noOps : ([rewrite.opsHeader] + ops).joined(separator: "\n")])
        } else {
            let ops = [(o.fillers, strict.fillers), (o.corrections, strict.corrections), (o.punctuation, strict.punctuation), (o.lists, strict.lists)].filter(\.0).map(\.1)
            rebuilt = fill(strict.frame, ["ops": ops.isEmpty ? strict.noOps : ([strict.opsHeader] + ops).joined(separator: "\n"), "spoken": o.spoken ? strict.spoken : ""])
        }
        expect(rebuilt == system(o), "rebuilt system prompt \(bits)")
        rebuilt += fill(o.polish ? polishTerms : strictTerms, ["terms": "A" + sep1 + "B"]) + fill(notes, ["id": id, "notes": "N"]) + contextSystem
        expect(rebuilt == system(o, terms: ["A", "B"], context: context, instructions: "N"), "rebuilt system prompt with tails \(bits)")
    }

    // TransformPrompt.
    let kinds: [(String, ModeTransform)] = [("aiPrompt", .aiPrompt), ("email", .email), ("notes", .notes)]
    func tsystem(_ k: ModeTransform, terms: [String] = [], instructions: String = "") -> String {
        TransformPrompt.build(k, transcript: "x", dictionaryTerms: terms, nonce: id, instructions: instructions).system
    }
    let bases = kinds.map { tsystem($0.1) }
    let tpre = commonPrefix(bases), tsuf = commonSuffix(bases.map { String($0.dropFirst(tpre.count)) })
    var purpose: [String: String] = [:], baseline: [String: String] = [:]
    for (k, b) in zip(kinds, bases) { purpose[k.0] = String(b.dropFirst(tpre.count).dropLast(tsuf.count)); baseline[k.0] = TransformPrompt.baseline(k.1) }
    let tterms = after(tsystem(.email, terms: [m1, m2]), bases[1]), tsep = between(tterms, m1, m2)
    let tnotes = placeholders(after(tsystem(.email, instructions: m3), bases[1])).replacingOccurrences(of: m3, with: "{notes}")
    let tuser = placeholders(TransformPrompt.build(.notes, transcript: m4, nonce: id, instructions: "").user).replacingOccurrences(of: m4, with: "{transcript}")
    let transform = Transform(
        previousAIPromptBaseline: TransformPrompt.previousAIPromptBaseline, aiPromptDetailRule: TransformPrompt.aiPromptDetailRule,
        previousDetailAIPromptBaseline: TransformPrompt.previousDetailAIPromptBaseline, aiPromptConditionRule: TransformPrompt.aiPromptConditionRule,
        previousConditionalAIPromptBaseline: TransformPrompt.previousConditionalAIPromptBaseline, previousEmailBaseline: TransformPrompt.previousEmailBaseline,
        previousNotesBaseline: TransformPrompt.previousNotesBaseline, previousStructuredAIPromptBaseline: TransformPrompt.previousStructuredAIPromptBaseline,
        previousStructuredEmailBaseline: TransformPrompt.previousStructuredEmailBaseline, previousStructuredNotesBaseline: TransformPrompt.previousStructuredNotesBaseline,
        baseline: baseline, frame: tpre + "{purpose}" + tsuf, purpose: purpose,
        terms: tterms.replacingOccurrences(of: m1 + tsep + m2, with: "{terms}"), termsSeparator: tsep, notes: tnotes, user: tuser)
    for (name, k) in kinds {
        let rebuilt = fill(transform.frame, ["purpose": purpose[name]!]) + fill(transform.terms, ["terms": "A" + tsep + "B"]) + fill(tnotes, ["id": id, "notes": "N"])
        expect(rebuilt == tsystem(k, terms: ["A", "B"], instructions: "N"), "rebuilt transform prompt \(name)")
        expect(fill(tuser, ["id": id, "transcript": "T"]) == TransformPrompt.build(k, transcript: "T", nonce: id).user, "transform user \(name)")
    }
    for s in [rewrite.frame, strict.frame, notes, cleanup.user, transform.frame, transform.notes] {
        let names = s.components(separatedBy: "{").dropFirst().map { $0.components(separatedBy: "}")[0] }
        expect(names.allSatisfy { ["ops", "spoken", "id", "notes", "sent", "purpose"].contains($0) }, "a prompt text contains a brace")
    }

    let enc = JSONEncoder()
    enc.outputFormatting = [.sortedKeys, .prettyPrinted, .withoutEscapingSlashes]
    print(String(decoding: try enc.encode(Export(cleanup: cleanup, transform: transform)), as: UTF8.self))
}}
