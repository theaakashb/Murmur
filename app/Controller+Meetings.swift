// AppController: the glue between the app and meetings (configuration, notes, starting a meeting).
import AppKit

extension AppController {
    // MARK: Meeting notes

    /// Meetings are stored in `history/meetings` and their chunk audio in `retry-audio/meetings`, so
    /// History's retention and Delete All Data cover them. They transcribe on this Mac only.
    func configureMeetings(_ locations: MurmurDataLocations) {
        let recorder = meetingRecorder
        // Without the helper and manifest (the UI fixture, or a build without them) the recorder cannot transcribe.
        var local: MeetingRecorder.LocalTranscription?
        if !preview, let runtime = helperRuntime {
            let warm = warm
            local = .init(
                transcribe: { [weak self] url, done in
                    guard let self else { done(nil); return }
                    runtime.launch(models: URL(fileURLWithPath: self.modelDirectory), qos: .utility) { launch in
                        guard let launch else { done(nil); return }
                        warm.transcribe(url, helper: runtime.helper, launch: launch) { done(helperAnswer($0)?["text"] as? String) }
                    }
                },
                transcribeTimed: { [weak self] url, done in
                    guard let self else { done(nil); return }
                    runtime.launch(models: URL(fileURLWithPath: self.modelDirectory), qos: .utility) { launch in
                        guard let launch else { done(nil); return }
                        warm.transcribeTimed(url, helper: runtime.helper, launch: launch) { line in
                            let answer = helperAnswer(line)
                            guard let text = answer?["text"] as? String else { done(nil); return }
                            done(ChunkText(text: text, words: answer?["words"] as? [[Double]]))
                        }
                    }
                },
                helper: runtime.helper)
        }
        recorder.wire(.init(
            store: try? MeetingStore(directory: locations.meetings),
            audioRoot: locations.meetingAudio,
            persists: appliedHistoryMode.persistsTranscripts,
            applyDictionary: { [weak self] text in self?.processing?.dictionary.apply(to: text) ?? text },
            notesRoute: { [weak self] in self?.meetingNotesRoute },
            notesWriter: { [weak self] meeting, lecture in
                guard let self else { throw NotesFailure.failed("Murmur is closing.") }
                return try await self.writeMeetingNotes(meeting, lecture: lecture)
            },
            verifySpeakerModel: speakerSettings.verifyModel,
            local: local))
        recorder.onLiveChange = { [weak self] in self?.updateMenu?(); self?.meetingPanelChanged?() }
        recorder.onNotesChoiceNeeded = { [weak self] in self?.screenRequest = .meetings; self?.openSettings?() }
        speakerSettings.attach(recorder, preview: preview) { [weak self] in self?.isBusy ?? true }
    }
    /// The disclosure, volume and cost note for Cloud, or nil when no cloud transcriber is configured.
    var meetingCloudNote: (disclosure: String, note: String)? {
        var routes = routeSettings; routes.transcription = .cloud
        let plan = RoutePlan.make(routes, cleanupSendsText: false) { _ in nil }
        guard let disclosure = plan.stages.first(where: { $0.stage == .transcription }) else { return nil }
        return (disclosure.summary, MeetingRoute.cloudNote(provider: disclosure.provider.displayName, host: disclosure.destinationHost))
    }

    /// The applied routes with transcription on this Mac and cleanup on a notes route's provider, for meeting notes.
    private func notesRoutes(_ choice: CleanupChoice) -> RouteSettings {
        var routes = appliedRoutes; routes.transcription = .local; routes.cleanup = choice
        return routes
    }
    /// Stands in for a key where only the disclosure is built (Settings must not read the Keychain).
    private static let disclosureOnlyKey = APIKey("disclosure-only")

    /// The notes route's provider, built from the applied settings and the Keychain like cleanup's,
    /// or why it cannot run. Never falls back to another route.
    func meetingNotesProvider(_ route: MeetingNotesRoute) -> (provider: CleanupProvider?, refusal: String?) {
        switch route {
        case .none: return (nil, "Meeting notes are off. Choose a notes route in Settings → Meetings.")
        case .appleIntelligence:
            let apple = AppleCleanupProvider()
            return apple.status().map { (nil, $0.userMessage) } ?? (apple, nil)
        case .customAPI: break
        }
        let plan = routePlan(notesRoutes(.customAPI), cleanupSendsText: true)
        guard let cleaner = plan.cleaner else { return (nil, (plan.refusal ?? "\(route.label) is not set up.") + " Set it up in Settings → Cleanup and API Keys.") }
        // A whole meeting takes longer than a dictation: allow the longest wait each route permits.
        return (cleaner.forMeetingNotes(), nil)
    }

    /// Why a notes route cannot run, for Settings. Checks only that a key exists, so rendering never
    /// reads the Keychain; the key is read when notes are written.
    func meetingNotesSetupProblem(_ route: MeetingNotesRoute) -> String? {
        switch route {
        case .none: return nil
        case .appleIntelligence: return AppleCleanupProvider().status()?.userMessage
        case .customAPI:
            let plan = RoutePlan.make(notesRoutes(.customAPI), cleanupSendsText: true) { self.credentials.exists($0) ? Self.disclosureOnlyKey : nil }
            return plan.cleaner == nil ? (plan.refusal ?? "\(route.label) is not set up.") + " Set it up in Settings → Cleanup and API Keys." : nil
        }
    }

    /// Who receives the transcript on a notes route, for Settings and the first-Stop question.
    func meetingNotesDisclosure(_ route: MeetingNotesRoute) -> String {
        guard route == .customAPI else { return MeetingNotesRoute.disclosure(route, host: nil, local: true, retention: nil) }
        let disclosure = RoutePlan.make(notesRoutes(.customAPI), cleanupSendsText: true) { _ in Self.disclosureOnlyKey }.stages.first
        let local = disclosure?.staysOnThisMac ?? false
        let warning = disclosure?.warning.map { " " + $0 } ?? ""
        return MeetingNotesRoute.disclosure(route, host: disclosure?.destinationHost, local: local, retention: (disclosure?.retentionNote ?? "") + warning)
    }

    /// Writes notes for a finished meeting on the route chosen now.
    func writeMeetingNotes(_ meeting: Meeting, lecture: Bool = false) async throws -> MeetingNotesWriter.Outcome {
        let route = meetingNotesRoute ?? .none
        let built = meetingNotesProvider(route)
        guard let provider = built.provider else { throw NotesFailure.failed(built.refusal ?? "Notes could not be written.") }
        let name = route == .appleIntelligence ? "Apple Intelligence (on-device)" : provider.id.displayName
        let structured = route == .appleIntelligence
        return try await MeetingNotesWriter.write(meeting, route: route, routeName: name, structured: structured, lecture: lecture) { system, user in
            do { return structured ? try await AppleSystemModel.notes(system: system, user: user, lecture: lecture) : try await provider.cleanup(CleanupMessages(system: system, user: user)) }
            catch ProviderError.payloadTooLarge, ProviderError.outputTruncated { throw NotesFailure.tooLarge }
            catch let error as ProviderError { throw NotesFailure.failed(error.userMessage) }
            catch { throw NotesFailure.failed("Notes could not be written.") }
        }
    }

    /// Start from the Meetings page, Home or the menu bar. Meetings need the local model.
    func startMeeting(_ source: MeetingSource) {
        var cloud: MeetingRoute.Cloud?, cloudName: String?
        if meetingTranscription == .cloud {
            // The configured cloud transcriber with its existing key; an unconfigured one refuses, never falls back.
            var routes = appliedRoutes; routes.transcription = .cloud
            let plan = routePlan(routes, cleanupSendsText: false)
            guard let provider = plan.transcriber else {
                meetingRecorder.message = (plan.refusal ?? "The cloud transcriber is not set up.") + " Set it up under Advanced in Settings → Transcription, or turn off the Custom Provider in Settings → Meetings."
                screenRequest = .meetings; openSettings?(); return
            }
            cloudName = provider.id.displayName
            cloud = { url, done in
                Task {
                    switch await CloudTranscription.run(provider: provider, audioFile: url) {
                    case .transcript(let text): done(.text(text))
                    case .noSpeech: done(.noSpeech)
                    case .failed, .cancelled: done(.failed)
                    }
                }
            }
        } else if !modelSlot.row.isReady {
            meetingRecorder.message = "Meeting notes transcribe on this Mac. Download the speech model in Settings → Transcription first."
            screenRequest = .meetings; openSettings?(); return
        }
        if !meetingRecorder.start(source, cloud: cloud, cloudName: cloudName) {
            if let pending = meetingRecorder.consentPending {
                meetingConsent.show(source: pending) { [weak self] acknowledged in
                    guard let self else { return }
                    self.meetingRecorder.consentPending = nil
                    if acknowledged { MeetingConsent.acknowledge(.standard); self.startMeeting(pending) }
                }
            } else {
                screenRequest = .meetings; openSettings?()
            }
        }
    }
}
