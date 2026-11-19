# Dictation pipeline

Derived from the Swift app at commit `c70cdb1`; references are `app/<file>:<line>` at that commit, a sample of which was re-checked against the source. This is a map of behavior to port, not a substitute for reading the Swift: port `CleanupGuard`, `RuleCleaner`, `Dictionary` and `Learning` from their source and checks.

Order of one dictation: **trigger → record → transcribe → save raw → dictionary → voice cues → mode → cleanup → guard → insert → History → learning.**

## 1. Trigger

- `ShortcutMachine` states `idle / listening / processing`; actions `start / finish / cancel` (Shortcut.swift:12-28). Hold threshold 0.250 s, double-press window 0.400 s (Shortcut.swift:28).
- Press from idle starts capture at once, so the first word is never lost. Release after ≥ 0.25 s finishes. A shorter tap latches toggle mode when `pressToToggle` is on; otherwise it waits for a second press, and is cancelled and discarded if none comes within 0.4 s. A press while listening finishes. Key repeat is ignored; presses during processing are ignored by the machine.
- Escape cancels while recording; during processing it cancels the cloud request (Controller.swift:563). Only an explicit cancel is kept for a 5 s Undo, in memory as 16 kHz mono floats, never written to disk (Controller.swift:779-792, Session.swift:122).
- The hook thread starts capture before the UI thread sees the press (Shortcut.swift:405-418). At release, audio is cut at release time plus a 0.2 s tail (Controller.swift:226, 588-591; Capture.swift:106).
- **Windows:** `WH_KEYBOARD_LL` on a dedicated thread with its own message loop; never let a busy UI thread delay it.

## 2. Start guards and snapshot

In order (Controller.swift:619-662): refuse on secure input; refuse if the microphone is not allowed; refuse on a route-plan refusal or missing local model files (an unconfigured cloud route never falls back to local); refuse if the focused element is a password field; no destination if Murmur itself has focus. The route, destination, mode and settings are snapshotted at start; changes mid-dictation don't apply to it.

**Windows:** there is no global Secure Input; the password check is UI Automation `IsPassword` on the focused element. The microphone check is the Windows privacy setting (capture activation fails with access denied).

## 3. Record

- WAV in the device's native format (Capture.swift:138-148); converted to 16 kHz mono for upload and the speech helper (AudioFormat.swift).
- Private file, UUID name, in `retry-audio/`, purged after 24 h (Capture.swift:451-470, Retention.swift:192).
- Hard cap 300 s, then auto-finish (Session.swift:15, 51-59). Start/stop cues (Controller.swift:824).
- Discard: no frames → "No audio was captured" (Session.swift:70, Controller.swift:677-679). A write failure keeps partial audio for retry (Controller.swift:269-273).
- Cloud pre-check only: skip if shorter than 0.3 s, or if the second-highest 100 ms window peak is below 0.01 (≈ −40 dBFS) (AudioFormat.swift:5-19, CloudAudio.swift:30-38). Local has no pre-check.
- Any route: a transcript with no letter or digit counts as no speech (Controller.swift:966, Routes.swift:282).

## 4. Transcribe

- Route plan (Routes.swift:236-267): local Parakeet, custom OpenAI-compatible (`cloud`), or Murmur Cloud. A cloud route with missing configuration or key is a refusal.
- **No fallback** between transcribers (Routes.swift:10-12, 301). A cloud failure ends in the error phase with audio kept; one request, no retry.
- Local (Controller.swift:880-960): verify model hashes before launch (ModelHelper.swift:44-55); use the warm helper, with chunks transcribed during recording; if it does not answer, run a one-shot helper with a 300 s kill timer. Dictionary bias is passed as `--ctc <model> --vocabulary parakeet-vocabulary.json` only when the dictionary has entries and the CTC model verifies (ModelHelper.swift:44-55). On Windows the equivalent arguments depend on the M3 hotwords spike (PLAN "Speech recognition").
- Cloud (CloudAudio.swift:17-27, Routes.swift:284-314): 16 kHz mono PCM16 WAV, trailing silence trimmed to 0.5 s; multipart POST `{base}/v1/audio/transcriptions` with `model`, `response_format` (`verbose_json` for Groq/OpenAI Whisper, else `json`), `temperature=0`, `language=en` (OpenAICompatible.swift:10-27). **No dictionary prompt.** Limit 25,000,000 bytes, deadline 75 s (OpenAICompatible.swift:185, 216). Murmur Cloud: same format, model `murmur-transcribe`, bearer auth (MurmurCloud.swift:12, 110-130).
- Provider errors and dispositions: ProviderTypes.swift:91-157. Only `cancelled` discards the audio.

## 5. Save raw

The raw transcript is written to History before cleanup (Controller.swift:970-976, 1015-1019), so a crash during cleanup loses nothing.

## 6. Dictionary and voice cues

- `DictionaryStore.apply` runs first in every style (Processing.swift:112-119, Dictionary.swift:247-285): whole-word variant phrases to the preferred spelling, longest first; phonetic matching for unfamiliar single words of 5+ letters when a lexicon is available. Limits: 100 characters, 6 words per variant (Dictionary.swift:89-90). Learned variants that are ordinary words are skipped (Dictionary.swift:214-236).
- Lexicon `isCommonWord` = `NSSpellChecker`, English, no misspelling found (Controller+Dictionary.swift:85-93). Windows: a bundled English word list (PLAN subsystem map). It must be callable from any thread on Windows.
- Voice cues match on the raw transcript first; each stretch between cues is processed on its own and cue expansions skip cleanup (Processing.swift:5-6, 131-134).

## 7. Mode

- Matched at start from the focused app, and from the page URL when a mode lists sites; site beats app, first match in list order wins (Controller.swift:687-701, WritingModes.swift:133-138).
- A trigger phrase at the start or end of the transcript overrides the mode and is removed; presets match only at the start (WritingModes.swift:157-198, Controller.swift:1038-1041). Presets: Prompt Mode, Email, Notes (WritingModes.swift:31-35).
- A mode runs on the AI engine (AIEngine.swift:7-50, Processing.swift:218); with no engine, only its non-AI settings apply.

## 8. Cleanup

- Local route, or a style that uses no model: synchronous rule cleaner (Controller.swift:1058-1061, Processing.swift:164-177). Otherwise one cancellable model call (Controller.swift:1062-1066).
- Options: fillers, corrections, lists, punctuation, polish, professional, spoken (CleanupTypes.swift:7-25). Only polish and professional use a model (WritingModes.swift:74).
- Prompt (CleanupPrompt.swift:104-140): transcript in a nonce-tagged `<transcript-NONCE>` block; spoken commands pre-applied by rules; up to 50 dictionary terms, then style notes, then screen context marked as data.
- Murmur Cloud: JSON `{transcript, level, fillers, corrections, punctuation, lists, terms, style_notes, context}` to `/v1/cleanup`, 45 s (MurmurCloud.swift:40-71). Custom: chat completions, `temperature=0`, timeout 120 s default, 300 s max (OpenAICompatible.swift:377-378).
- Long text is split into sections run in parallel, each guarded on its own (Processing.swift:253-262).

## 9. Guard and fallback

- Rejection reasons: CleanupTypes.swift:154-169. Trusted mode (polish/professional with `trustModel`) rejects only empty output, answer phrases, output longer than 2×+10, and truncation. Everything else gets the strict checks: answer and assistant phrases, added markdown, edited quotes, length ratio, word edit distance, numbers added or dropped, negation count, names added or dropped, inserted-word limits (CleanupGuard.swift:458-1006, Cleanup.swift:97-120). Local models are always strict (Processing.swift:287).
- **Trusted routes and screen context (main `800dc99`, PR #96):**
  - The guard judges against the transcript section only (`Processing.swift:336-339`, `cloudClean` at :340), so screen context never widens what it accepts.
  - On a trusted route (polish or professional on a cloud or custom model), though, the guard holds only malfunctions. It therefore does not stop words taken from screen context reaching the paste.
  - The defense there is the prompt's data-only tags (`CleanupPrompt`, already ported). The full guard is deliberately not applied: a History replay (Nov 2026) found it would hold 23% of good polish written with context.
  - M2's `Processing` port must keep exactly this behavior, with no extra screen-context guard on trusted routes and no relaxed guard elsewhere. The Settings copy for screen context must say that on trusted routes words from the screen can appear in the paste.
- On rejection or provider error (Processing.swift:12-22, 288-300): first try the model's punctuation on the locally cleaned text if it passes the punctuation-only guard; else local rules; else the dictionary-applied text. A dictation is never held. Tone is applied last (Processing.swift:174-179). If only fillers were heard, nothing is inserted and no error is shown (Controller.swift:1128-1133).

## 10. Insert (paste only)

- Focus is captured at start and again just before paste; the paste goes where the cursor is now (Controller.swift:1155-1156, Insertion.swift:114-156).
- Leading space when the previous character is not whitespace or an opening bracket, the selection is empty, and the next character is not a letter or digit (Insertion.swift:182-192), when `intelligentLeadingSpace` is on.
- Delivery bracket: write `insertion-pending`, paste once, write the final status (Delivery.swift:11-20, Controller.swift:1158-1160). Never retried.
- Paste (Insertion.swift:241-323): save every clipboard item, stage the text with transient markers and an ownership token, re-check the destination, post the paste chord. Restore the saved clipboard 0.5 s later (`clipboardLease`, Insertion.swift:222) only if the clipboard still holds our item. Flush a pending restore before the next paste and at quit.
- Readable fields are read back for up to 0.5 s every 25 ms to mark `verified`/`unverified`; this only feeds History and learning (Insertion.swift:194-218, 346-368).
- Failures: password field, focus changed, clipboard could not be saved or changed, destination changed after staging (Insertion.swift:200-306). The user sees "Could not paste the dictation…" with a Copy card for 30 s (Controller+Recovery.swift:133-141). With `automaticInsertion` off: phase `saved`, card "Saved, not pasted" (Controller+Recovery.swift:143-148).
- Paste last (Mac ⌃⌘V, Shortcut.swift:245): pastes the in-memory `lastDictationText` into the frontmost app; ignored while busy (Controller+Recovery.swift:125-131). ⌃⌘C copies the raw transcript (Controller+History.swift:17-22); ⌃⌘Z reverts to raw when offered (Shortcut.swift:230).
- **Windows:** Ctrl+V via `SendInput`; clipboard-history and cloud-sync exclusion formats on our item (PLAN "Insertion"); UIPI blocks pasting into elevated windows.

## 11. History

- Fields (History.swift:12-70): id, recordedAt, rawText, finalText, insertionStatus, cleanupFallback, cleanupRejectedOutput, seconds, app, cleanupOptions, mode id and name, transcription and cleanup provider and model, appVersion, contextUsed, cleanupOutcome, edited, cues, transcription detail, cleanup detail, afterPaste, timing, recordingFile, editOriginal.
- Statuses (History.swift:6-8): `saved-not-inserted, insertion-pending, held-cleanup, held-transcript, copied, failed, verified, unverified, edited`.
- Three writes per dictation: raw, pending, final. Created exclusively, private (History.swift:189-200). History off keeps entries in memory only (Retention.swift:198-201). Retention purge at launch (Controller.swift:361).
- The Windows History JSON uses the same field names, so the two apps' files and backups match.

## 12. Learning

- After a successful insertion that is not a cue expansion, when learning is on (Controller.swift:1168-1169, Controller+Learning.swift:35).
- Watch the field up to 60 s, until focus leaves or the next insertion (Observation.swift:54, Controller+Learning.swift:7-9); poll every 0.25 s, settle after 2 s of stable text. The Mac falls back to following keystrokes through a listen-only tap when the field can't be read; nothing typed is stored.
- Edits pass `CorrectionFilter` (Learning.swift:39-70): structure check, meaning guards, respelling similarity (0.75, or 0.5 with a phonetic match), lexicon guard. One settled fix promotes (Learning.swift:364); the user sees a "learned" notice with Undo. Editing a History entry also learns (Controller+History.swift:34-45).
- **Windows:** read-back through UI Automation; the keystroke fallback is ported, scoped to the paste target for a bounded time and never stored (PLAN "Learning from edits").

## 13. Voice edit

- Separate shortcut, or the dictation key when a readable selection exists (EditTrigger.swift:7-14, VoiceEdit.swift:175-182; selection probe timeout 0.15 s).
- Needs an AI engine; refuses secure input, password fields, Murmur's own window, no selection (VoiceEdit.swift:59-85). Selection limit 8,000 characters remote (3,000 on-device) (EditPrompt.swift:9-10). The selection is read through accessibility, never by sending a copy chord. Instruction = the dictated transcript, 500 characters max (VoiceEdit.swift:100).
- Prompt with nonce-tagged selection and instruction (EditPrompt.swift:13-22); Murmur Cloud uses its edit endpoint (MurmurCloudEdit.swift).
- `EditCheck.verdict`: unchanged, refused (empty, longer than 3×+200, assistant-style) or apply (EditPrompt.swift:29-45).
- Replace only if the element, value, selection range and selected text are unchanged, through the normal paste (VoiceEdit.swift:152-160). Status `edited`, `editOriginal` saved, card offers the original for Copy.

## 14. Phases and bubble

- `Phase`: ready, listening, processing, error, saved (Controller.swift:25). Stage: transcription, cleanup.
- The bubble shows for listening, processing and error (Bubble.swift:6-8), plus the Undo notice, mode-recovery chip, learned notice and paste-failure card. Auto-dismiss: 2 s quick, 5 s default, 8 s error, 30 s for a card (Controller.swift:857-862).

## 15. Concurrency

- The controller is UI-thread bound (Controller.swift:34). Capture, hook, helper processes, file I/O and HTTP run off the UI thread.
- Every stage compares a per-session generation token and drops stale results; Escape, shutdown and a new session renew it.
- `CloudRequest` wraps one task and guarantees nothing is delivered after cancel (Routes.swift:416-446).
- Prewarm: the warm speech helper starts at launch and after model or dictionary changes (Controller.swift:388, 1268-1277).
- **Windows:** one `SynchronizationContext` for the controller, `CancellationToken` per session alongside the generation token.

## Not covered here

Live words, meetings, tips, insights, Chunker internals, the capture callback and the exact History file format were not traced for this document; port them from source.
