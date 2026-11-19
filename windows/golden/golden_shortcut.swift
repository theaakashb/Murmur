// deps: Shortcut
import Foundation

// Golden outputs of ShortcutMachine (hold, double press, press-to-toggle, cancel, button start, processing) over scripted and seeded random
// event sequences with injected time. An op is [name] or [name, time] (press also takes "repeat"); each result is the action (or "-" for none,
// "true"/"false" for beginProcessing), and each state is "<state>/<awaitingSecondPress>" after the op. One case per line.
// Run from the repository root.
struct Case: Encodable { var label: String, pressToToggle: Bool, hold: Double, window: Double, ops: [[String]], results: [String], states: [String] }

func lines<T: Encodable>(_ name: String, _ items: [T]) -> String {
    let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
    return "\"\(name)\": [\n" + items.map { String(decoding: try! encoder.encode($0), as: UTF8.self) }.joined(separator: ",\n") + "\n]"
}

func run(_ label: String, toggle: Bool = false, hold: Double = 0.25, window: Double = 0.4, _ ops: [[String]]) -> Case {
    var machine = ShortcutMachine(holdThreshold: hold, doublePressWindow: window)
    machine.pressToToggle = toggle
    var results: [String] = [], states: [String] = []
    for op in ops {
        let time = op.count > 1 ? Double(op[1])! : 0
        let action: ShortcutMachine.Action?
        switch op[0] {
        case "press": action = machine.press(at: time, isRepeat: op.count > 2 && op[2] == "repeat")
        case "release": action = machine.release(at: time)
        case "expire": action = machine.doublePressExpired(at: time)
        case "cancel": action = machine.cancel()
        case "finish": action = machine.finish()
        case "startToggled": action = machine.startToggled()
        case "lost": action = machine.eventStreamLost()
        case "processingFinished": machine.processingFinished(); action = nil
        case "beginProcessing": results.append(String(machine.beginProcessing())); states.append("\(machine.state.rawValue)/\(machine.awaitingSecondPress)"); continue
        default: fatalError(op[0])
        }
        results.append(action?.rawValue ?? "-")
        states.append("\(machine.state.rawValue)/\(machine.awaitingSecondPress)")
    }
    return Case(label: label, pressToToggle: toggle, hold: hold, window: window, ops: ops, results: results, states: states)
}

@main struct Gen {
    static func main() throws {
        var cases: [Case] = []
        cases.append(run("hold records until release", [["press", "1"], ["release", "1.6"], ["processingFinished"]]))
        cases.append(run("hold exactly at the threshold", [["press", "1"], ["release", "1.25"]]))
        cases.append(run("a tap just under the threshold waits for a second press", [["press", "1"], ["release", "1.249"], ["expire", "1.5"], ["expire", "1.65"]]))
        cases.append(run("double press latches a toggle", [["press", "3"], ["release", "3.1"], ["press", "3.3"], ["release", "3.4"], ["press", "9"], ["release", "9.1"], ["processingFinished"]]))
        cases.append(run("a lone tap is cancelled", [["press", "1"], ["release", "1.1"], ["expire", "1.3"], ["expire", "1.5"]]))
        cases.append(run("press-to-toggle: tap starts, next press finishes", toggle: true, [["press", "1"], ["release", "1.1"], ["press", "4"], ["release", "4.1"]]))
        cases.append(run("press-to-toggle: a hold still finishes on release", toggle: true, [["press", "1"], ["release", "2"]]))
        cases.append(run("key repeats and a second down are ignored", [["press", "1"], ["press", "1.05", "repeat"], ["press", "1.1"], ["release", "1.5"]]))
        cases.append(run("presses while processing do nothing", [["press", "1"], ["release", "2"], ["press", "2.1"], ["release", "2.2"], ["processingFinished"], ["press", "3"]]))
        cases.append(run("button start", [["startToggled"], ["startToggled"], ["release", "1"], ["press", "2"], ["release", "2.1"], ["startToggled"], ["processingFinished"], ["startToggled"], ["finish"], ["processingFinished"]]))
        cases.append(run("no button start while a key is down", [["press", "5"], ["startToggled"]]))
        cases.append(run("cancel while listening keeps the press latch", [["press", "1"], ["cancel"], ["press", "1.2"], ["release", "1.5"], ["press", "2"]]))
        cases.append(run("cancel and finish when idle", [["cancel"], ["finish"], ["expire", "9"], ["release", "1"]]))
        cases.append(run("event stream lost", [["press", "1"], ["lost"], ["press", "1.1"], ["release", "1.2"], ["lost"]]))
        cases.append(run("undo processing", [["beginProcessing"], ["press", "1"], ["beginProcessing"], ["processingFinished"], ["beginProcessing"], ["processingFinished"], ["press", "2"]]))
        cases.append(run("custom thresholds", hold: 1, window: 2, [["press", "0"], ["release", "0.9"], ["expire", "2.8"], ["expire", "2.9"], ["press", "3"], ["release", "4"]]))
        var seed: UInt64 = 20_260_102
        func next(_ bound: Int) -> Int { seed = seed &* 6364136223846793005 &+ 1442695040888963407; return Int((seed >> 33) % UInt64(bound)) }
        let names = ["press", "press", "press", "release", "release", "release", "expire", "cancel", "finish", "startToggled", "processingFinished", "beginProcessing", "lost", "press-repeat"]
        for n in 0..<300 {
            var time = 0.0
            var ops: [[String]] = []
            for _ in 0..<(8 + next(30)) {
                time += [0.0, 0.05, 0.1, 0.2, 0.24, 0.25, 0.26, 0.39, 0.4, 0.41, 0.7, 2.0][next(12)]
                let name = names[next(names.count)]
                switch name {
                case "press-repeat": ops.append(["press", String(time), "repeat"])
                case "press", "release", "expire": ops.append([name, String(time)])
                default: ops.append([name])
                }
            }
            cases.append(run("random \(n)", toggle: next(3) == 0, ops))
        }
        print("{\n" + lines("cases", cases) + "\n}")
    }
}
