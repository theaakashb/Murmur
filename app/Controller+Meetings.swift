// AppController: the glue between the app and meetings (configuration, notes, starting a meeting).
import AppKit

extension AppController {
    // MARK: Meeting notes (PRD section 11)

    /// Meetings are stored in `history/meetings` and their chunk audio in `retry-audio/meetings`, so
    /// History's retention and Delete All Data cover them. They transcribe on this Mac only (Q2).
    func configureMeetings(base: URL) {
        let recorder = meetingRecorder
        // Without the helper and manifest (the UI fixture, or a build without them) the recorder cannot transcribe.
        var local: MeetingRecorder.LocalTranscription?
        if !preview, let helper = Bundle.main.url(forAuxiliaryExecutable: "ModelBench"), let manifest = Bundle.main.url(forResource: "artifact-pins", withExtension: "json") {
            let warm = warm
            local = .init(
                transcribe: { [weak self] url, done in
                    guard let self else { done(nil); return }
                    let models = URL(fileURLWithPath: self.modelDirectory), vocabulary = self.vocabularyFile, dictionaryModel = self.dictionaryModelDirectory
                    DispatchQueue.global(qos: .utility).async {
                        guard (try? ModelVerificationCache.shared.verify(models, manifest: manifest)) != nil,
                              let launch = HelperLaunch.make(models: models, manifest: manifest, vocabulary: vocabulary, dictionaryModel: dictionaryModel) else { done(nil); return }
                        warm.transcribe(url, helper: helper, launch: launch) { line in
                            let answer = line.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
                            done(answer?["text"] as? String)
                        }
                    }
                },
                transcribeTimed: { [weak self] url, done in
                    guard let self else { done(nil); return }
                    let models = URL(fileURLWithPath: self.modelDirectory), vocabulary = self.vocabularyFile, dictionaryModel = self.dictionaryModelDirectory
                    DispatchQueue.global(qos: .utility).async {
                        guard (try? ModelVerificationCache.shared.verify(models, manifest: manifest)) != nil,
                              let launch = HelperLaunch.make(models: models, manifest: manifest, vocabulary: vocabulary, dictionaryModel: dictionaryModel) else { done(nil); return }
                        warm.transcribeTimed(url, helper: helper, launch: launch) { line in
                            let answer = line.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
                            guard let text = answer?["text"] as? String else { done(nil); return }
                            done(ChunkText(text: text, words: answer?["words"] as? [[Double]]))
                        }
                    }
                },
                helper: helper)
        }
        recorder.wire(.init(
            store: try? MeetingStore(directory: base.appendingPathComponent("history").appendingPathComponent("meetings")),
            audioRoot: base.appendingPathComponent("retry-audio").appendingPathComponent("meetings"),
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
        guard let disclosure = plan.stages.first(where: { $0.disclosure.stage == .transcription })?.disclosure else { return nil }
        return (disclosure.summary, MeetingRoute.cloudNote(provider: disclosure.provider.displayName, host: disclosure.destinationHost, storesAudio: routes.cloudTranscriber == .gemini))
    }

    /// The notes route's provider, built from the applied settings and the Keychain like cleanup's,
    /// or why it cannot run. Never falls back to another route.
    func meetingNotesProvider(_ route: MeetingNotesRoute) -> (provider: CleanupProvider?, refusal: String?) {
        let choice: CleanupChoice
        switch route {
        case .none: return (nil, "Meeting notes are off. Choose a notes route in Settings → Meetings.")
        case .appleIntelligence:
            let apple = AppleCleanupProvider()
            return apple.status().map { (nil, $0.userMessage) } ?? (apple, nil)
        case .gemini: choice = .gemini
        case .customAPI: choice = .customAPI
        }
        var routes = appliedRoutes; routes.transcription = .local; routes.cleanup = choice
        let plan = routePlan(routes, cleanupSendsText: true)
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
        case .gemini, .customAPI:
            var routes = appliedRoutes; routes.transcription = .local; routes.cleanup = route == .gemini ? .gemini : .customAPI
            let plan = RoutePlan.make(routes, cleanupSendsText: true) { self.credentials.exists($0) ? APIKey("disclosure-only") : nil }
            return plan.cleaner == nil ? (plan.refusal ?? "\(route.label) is not set up.") + " Set it up in Settings → Cleanup and API Keys." : nil
        }
    }

    /// Who receives the transcript on a notes route, for Settings and the first-Stop question.
    func meetingNotesDisclosure(_ route: MeetingNotesRoute) -> String {
        guard route == .gemini || route == .customAPI else { return MeetingNotesRoute.disclosure(route, host: nil, local: true, retention: nil) }
        var routes = appliedRoutes; routes.transcription = .local; routes.cleanup = route == .gemini ? .gemini : .customAPI
        let disclosure = RoutePlan.make(routes, cleanupSendsText: true) { _ in APIKey("disclosure-only") }.stages.first?.disclosure
        let local = disclosure?.staysOnThisMac ?? false
        let warning = disclosure?.warning.map { " " + $0 } ?? ""
        return MeetingNotesRoute.disclosure(route, host: disclosure?.destinationHost, local: local, retention: (disclosure?.retentionNote ?? "") + warning)
    }

    /// Writes notes for a finished meeting on the route chosen now (PRD 11.6).
    func writeMeetingNotes(_ meeting: Meeting, lecture: Bool = false) async throws -> MeetingNotesWriter.Outcome {
        let route = meetingNotesRoute ?? .none
        let built = meetingNotesProvider(route)
        guard let provider = built.provider else { throw NotesFailure.failed(built.refusal ?? "Notes could not be written.") }
        let name = route == .appleIntelligence ? "Apple Intelligence (on-device)" : provider.id.displayName
        let structured = route == .appleIntelligence
        return try await MeetingNotesWriter.write(meeting, route: route, routeName: name, structured: structured, lecture: lecture) { system, user in
            do { return structured ? try await AppleSystemModel.notes(system: system, user: user, lecture: lecture) : try await provider.cleanup(CleanupMessages(system: system, user: user)) }
            catch ProviderError.payloadTooLarge { throw NotesFailure.tooLarge }
            catch let error as ProviderError { throw NotesFailure.failed(error.userMessage) }
            catch { throw NotesFailure.failed("Notes could not be written.") }
        }
    }

    /// Start from the Meetings page, Home or the menu bar. Meetings need the local model (Q2).
    func startMeeting(_ source: MeetingSource) {
        var cloud: MeetingRoute.Cloud?, cloudName: String?
        if meetingTranscription == .cloud {
            // The configured cloud transcriber with its existing key; an unconfigured one refuses, never falls back.
            var routes = appliedRoutes; routes.transcription = .cloud
            let plan = routePlan(routes, cleanupSendsText: false)
            guard let provider = plan.transcriber else {
                meetingRecorder.message = (plan.refusal ?? "The cloud transcriber is not set up.") + " Set it up in Settings → Transcription, or choose On this Mac in Settings → Meetings."
                screenRequest = .meetings; openSettings?(); return
            }
            let vocabulary = processing?.dictionary.vocabularyTerms ?? []
            cloudName = provider.id.displayName
            cloud = { url, done in
                Task {
                    switch await CloudTranscription.run(provider: provider, audioFile: url, vocabulary: vocabulary) {
                    case .transcript(let text): done(.text(text))
                    case .noSpeech: done(.noSpeech)
                    case .heldAnswerSuspected, .failed, .cancelled: done(.failed)
                    }
                }
            }
        } else if !modelRow.isReady {
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
