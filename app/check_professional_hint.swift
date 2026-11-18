import Foundation

nonisolated(unsafe) var failures = 0
func check(_ condition: Bool, _ name: String) { if !condition { failures += 1; print("FAIL: \(name)") } }

@main struct ProfessionalHintChecks {
    static func main() {
        let filler = String(repeating: "and then we talked about the plan ", count: 6) // 42 words, no sentence end
        let runOn = filler + "no I mean the other plan"
        check(ProfessionalSignal.reasons(runOn) == [.runOn, .correction], "a long run-on with a correction cue signals both")
        check(ProfessionalSignal.reasons("Let's meet Thursday at the cafe.").isEmpty, "a short clean sentence signals nothing")
        check(ProfessionalSignal.reasons(filler).isEmpty, "a run-on alone, under the long bar, is one signal and does not fire")
        check(ProfessionalSignal.reasons(filler + filler).contains(.runOn), "a 70-word run-on fires alone")
        check(ProfessionalSignal.reasons("First we ship. Second we test. Finally we write up.").isEmpty, "list words alone are one signal and do not fire")
        check(ProfessionalSignal.reasons("First we ship. Second we test. Scratch that, we write up.") == [.correction, .list], "a list with a correction fires")
        check(ProfessionalSignal.reasons("um so uh the thing um is ready no wait it is not") == [.correction, .fillers], "fillers plus a cue")
        check(ProfessionalSignal.sentence([.runOn, .correction]) == "Professional would break this long run-on into clear sentences and keep only what you meant after your correction.", "sentence joins reasons")

        let day = 86400.0, start = Date(timeIntervalSince1970: 1_800_000_000)
        var s = ProfessionalHintState()
        func run(_ s: inout ProfessionalHintState, _ n: Int, at: Date, enabled: Bool = true, active: Bool = false) {
            for i in 0..<n { s.consider(entryID: "e\(i)", raw: runOn, now: at, enabled: enabled, professionalActive: active) }
        }
        run(&s, 12, at: start)
        check(s.offerID == nil && s.dictations == 12, "no offer before three days of use")
        run(&s, 1, at: start.addingTimeInterval(3 * day))
        check(s.offerID == "e0" && s.lastOffered != nil, "offers once the gate opens")
        let open = s; run(&s, 3, at: start.addingTimeInterval(4 * day))
        check(s.offerID == open.offerID && s.lastOffered == open.lastOffered, "an open offer is not replaced")
        s.offerID = nil; run(&s, 1, at: start.addingTimeInterval(5 * day))
        check(s.offerID == nil, "no second offer within seven days")
        run(&s, 1, at: start.addingTimeInterval(11 * day))
        check(s.offerID != nil, "a second offer after seven days")
        s.offerID = nil; s.dismissals = 2; run(&s, 1, at: start.addingTimeInterval(30 * day))
        check(s.offerID == nil, "two dismissals silence it")
        var off = ProfessionalHintState(dictations: 20, firstSeen: start)
        run(&off, 1, at: start.addingTimeInterval(9 * day), enabled: false)
        check(off.offerID == nil, "the Suggestions switch silences it")
        run(&off, 1, at: start.addingTimeInterval(9 * day), active: true)
        check(off.offerID == nil, "no offer while Professional is already on")
        var acted = ProfessionalHintState(dictations: 20, firstSeen: start, acted: true)
        run(&acted, 1, at: start.addingTimeInterval(9 * day))
        check(acted.offerID == nil, "acting on an offer ends the hint")
        if failures == 0 { print("Professional hint checks passed.") } else { exit(1) }
    }
}
