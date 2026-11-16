// AppController: collects the content-free diagnostics for "Report an Issue" (Diagnostics.swift). Reads settings and enum names, never text.
import AppKit

extension AppController {
    func diagnostics() -> Diagnostics {
        let info = Bundle.main.infoDictionary
        let style = cleanupStyle
        let choice = style.needsModel ? cleanupChoice : .localRules
        let last = appliedHistoryMode.persistsTranscripts ? history.first : nil
        return Diagnostics(
            appVersion: info?["CFBundleShortVersionString"] as? String ?? "unknown", build: info?["CFBundleVersion"] as? String ?? "unknown",
            macOS: ProcessInfo.processInfo.operatingSystemVersionString, macModel: Self.macModelIdentifier(),
            microphone: permission().rawValue, accessibilityAllowed: accessibilityAuthorized, inputMonitoringAllowed: inputMonitoringAuthorized,
            transcription: transcriptionRoute == .local ? .thisMac(model: ModelFacts.batch.repository) : cloudTranscriber == .customAPI ? .custom : .service(name: cloudTranscriber.displayName, model: nil),
            cleanup: choice == .customAPI ? .custom : .service(name: choice.displayName, model: choice == .localModel ? LocalCleanupIdentity.modelID : nil), cleanupStyle: style.label,
            historyOn: appliedHistoryMode.persistsTranscripts, retention: appliedRetention.label,
            models: modelSlots.map { let slot = self[keyPath: $0]; return (slot.facts.displayName, slot.row.isReady) },
            lastResult: last.map { entry in
                Diagnostics.LastResult(insertionStatus: entry.status?.rawValue ?? "unknown", cleanupFailure: entry.cleanupFallback.flatMap(CleanupFailureReason.init(rawValue:))?.rawValue,
                                       cleanupPath: entry.cleanupOutcome?.rawValue, totalSeconds: entry.timing?.total)
            })
    }

    private static func macModelIdentifier() -> String {
        var size = 0
        sysctlbyname("hw.model", nil, &size, nil, 0)
        var bytes = [CChar](repeating: 0, count: size)
        return sysctlbyname("hw.model", &bytes, &size, nil, 0) == 0 ? String(cString: bytes) : "unknown"
    }
}
