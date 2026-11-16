// Where the welcome guide is and which version of it was finished. Foundation only.
import Foundation

/// Where the guide is and which version of it was finished, in UserDefaults. Raise `version` when the
/// guide changes enough that everyone should see it again.
enum WelcomeGuide {
    static let version = 1
    enum Step: Int, CaseIterable {
        // Keep saved step IDs stable when adding choices before the model and test.
        case microphone, accessibility, inputMonitoring, model, tryIt, transcription, cleanup, meetings
        static let allCases: [Step] = [.microphone, .accessibility, .inputMonitoring, .transcription, .cleanup, .meetings, .model, .tryIt]
        var isConfiguration: Bool { self == .transcription || self == .cleanup || self == .meetings }
    }
    static let stepKey = "welcomeGuideStep", completedKey = "welcomeGuideCompletedVersion"

    private static let reviewedKey = "welcomeGuideReviewedChoices"
    static func confirm(_ step: Step, _ defaults: UserDefaults) {
        guard step.isConfiguration else { return }
        let reviewed = defaults.array(forKey: reviewedKey) as? [Int] ?? []
        defaults.set(Array(Set(reviewed + [step.rawValue])), forKey: reviewedKey)
    }
    static func isConfirmed(_ step: Step, _ defaults: UserDefaults) -> Bool {
        (defaults.array(forKey: reviewedKey) as? [Int] ?? []).contains(step.rawValue)
    }
    static func nextUnconfirmed(_ defaults: UserDefaults) -> Step? {
        Step.allCases.first { $0.isConfiguration && !isConfirmed($0, defaults) }
    }

    /// The step to reopen at, clamped so a step removed in a later version cannot strand the guide.
    static func resumeStep(_ defaults: UserDefaults) -> Step {
        Step(rawValue: defaults.integer(forKey: stepKey)) ?? (defaults.integer(forKey: stepKey) < 0 ? .microphone : .tryIt)
    }
    static func save(_ step: Step, _ defaults: UserDefaults) { defaults.set(step.rawValue, forKey: stepKey) }
    /// Installs from before the guide recorded completion count as having finished version 1 when they
    /// are already set up, so an update does not walk them through it again.
    static func completedVersion(_ defaults: UserDefaults, alreadySetUp: Bool) -> Int {
        if defaults.object(forKey: completedKey) != nil { return defaults.integer(forKey: completedKey) }
        return alreadySetUp && defaults.object(forKey: stepKey) == nil ? 1 : 0
    }
    static func opensAtLaunch(_ defaults: UserDefaults, alreadySetUp: Bool) -> Bool {
        let done = completedVersion(defaults, alreadySetUp: alreadySetUp)
        // Record the upgrade default, so saving a step later does not undo it.
        if done > 0 && defaults.object(forKey: completedKey) == nil { defaults.set(done, forKey: completedKey) }
        return done < version
    }
    static func complete(_ defaults: UserDefaults) {
        defaults.set(version, forKey: completedKey); defaults.removeObject(forKey: stepKey)
    }
    /// Replay from the menu starts over; finishing it again records the version again.
    static func replay(_ defaults: UserDefaults) { defaults.removeObject(forKey: reviewedKey); save(.microphone, defaults) }
}
