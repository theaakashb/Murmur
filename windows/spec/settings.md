# Settings, credentials and files

Derived from the Swift app at commit `c70cdb1`. References are `app/<file>:<line>` at that commit; a sample of them was re-checked against the source. If the Swift code changes, the Swift code wins and this file is updated.

## Windows rules

- One JSON settings file, `%LOCALAPPDATA%\Murmur\settings.json`, written atomically as a private file. Same key names and defaults as below, so the two apps' settings and backups read the same way.
- A missing or wrongly typed value reads as the default (`Settings.swift:14-21`).
- **No migrations are ported.** Windows starts fresh, so every row in the Mac's migration list (`Routes.swift:141-195`, `WritingModes.swift:86-120`, `WelcomeGuide.swift:46-53`, legacy Keychain accounts) is left out.
- Values that are Mac-specific in form get a Windows form under the same key: `inputDeviceUID` holds a WASAPI endpoint ID, `shortcut`/`voiceEditShortcut` hold Windows virtual-key codes, `callDetectionNever` holds lowercased process names, `meetingPanelTopRight` holds screen pixels.

## Settings

| key | type | default | meaning | Mac ref | Windows |
| --- | --- | --- | --- | --- | --- |
| cleanupFillers | bool | true | Remove filler words | Settings.swift:41 | same |
| cleanupCorrections | bool | true | Apply spoken self-corrections | Settings.swift:42 | same |
| cleanupLists | bool | true | Format spoken lists | Settings.swift:43 | same |
| cleanupPunctuation | bool | true | Punctuation and capitalization | Settings.swift:44 | same |
| cleanupPolish | bool | false | Polish (model routes); forced on by professional | Settings.swift:45, Controller+Settings.swift:52 | same |
| cleanupProfessional | bool | false | Professional rewrite | Settings.swift:46 | same |
| cleanupSpokenCommands | bool | true | Spoken commands and numbers | Settings.swift:47 | same |
| cleanupRoute | `local`/`cloud` | `local` | Derived from cleanupChoice at launch | Settings.swift:48, Controller.swift:352-353 | derive, don't store, if nothing else reads it |
| learningEnabled | bool | true | Learn corrections | Settings.swift:49 | same |
| learningRequiresReadableField | bool | false | Learn only from readable fields | Settings.swift:50 | same (UI Automation read-back) |
| cleanupScreenContext | bool | false | Send screen context to AI cleanup | Settings.swift:52 | same |
| historyMode | `on`/`off` | `on` | Save History to disk | Settings.swift:53, Retention.swift:198-199 | same |
| updateChecksEnabled | bool | true | Automatic update checks | Settings.swift:54 | Velopack check schedule |
| retention | `30-days`/`six-months`/`forever` | `30-days` | History retention; unknown reads as 30 days | Settings.swift:55, Retention.swift:13-17 | same |
| showMenuBar | bool | true | Menu bar item | Settings.swift:56 | not ported: the tray icon is always shown in v1 |
| showInDock | bool | false | Dock icon | Settings.swift:58 | not ported |
| inputDeviceUID | string | "" (system default) | Microphone | Settings.swift:59 | WASAPI endpoint ID |
| intelligentLeadingSpace | bool | false | Leading space before inserted text | Settings.swift:60 | same |
| automaticInsertion | bool | true | Paste automatically | Settings.swift:61 | same |
| pressToToggle | bool | false | Short tap latches recording | Settings.swift:62 | same |
| recordingSounds | bool | true | Start and stop sounds | Settings.swift:63 | same |
| keepRecordings | bool | false | Dead on the Mac: never read | Settings.swift:64 | not ported |
| liveWords | bool | false | Live words in the bubble | Settings.swift:65 | same |
| liveWordsOnCloud | bool | false | Live words also on cloud routes | Settings.swift:66 | same |
| settingsSection | int | 0 | Last Settings pane | Settings.swift:68 | same |
| sidebarCollapsed | bool | false | Settings sidebar collapsed | Settings.swift:69 | same |
| showDetailedSettingsInfo | bool | false | Detailed settings info | Settings.swift:70 | same |
| meetingPanelCollapsed | bool | false | Meeting panel minimized | Settings.swift:71 | same |
| dictationIndicator | `notch`/`bottom` | `notch` | Bubble position | Settings.swift:72 | Windows has no notch: values `top`/`bottom`, default open |
| meetingIndicator | `notch`/`menu-bar` | `notch` | Minimized meeting indicator | Settings.swift:73 | `top`/`tray`, default open |
| meetingPanelTopRight | [double] | [] | Meeting panel corner | Settings.swift:75 | screen pixels |
| openAtLoginDefaulted | bool | false | Start at login set on first launch | Settings.swift:77, main.swift:28-29 | same, with `HKCU\…\Run` |
| dictionarySuggestionsDismissed | [string] | [] | Dismissed suggestion words; removed by Delete All | Settings.swift:79 | same |
| shortcut | string | `right-option` | Dictation key preset or `custom:KEYCODE:FLAGS:KEY` | Controller+Settings.swift:8, Shortcut.swift:113-116 | Windows presets and VK codes; default `right-ctrl` |
| pasteLastShortcut | string | Mac fixed ⌃⌘V | Paste last dictation | Shortcut.swift:245 | new key on Windows, default `ctrl-win-shift-v` |
| voiceEditShortcut | string | `same` | Voice edit key; `same` = dictation key with a selection | Controller+Settings.swift:9, EditTrigger.swift:13-14 | same semantics |
| modelDirectory | path | computed under the user cache folder | Batch model folder | Controller+Settings.swift:10, ModelText.swift:97-100 | `%LOCALAPPDATA%\Murmur\Models\<model>-<rev>` |
| transcriptionRoute | `local`/`cloud`/`murmur-cloud` | `local` | Transcription route | Routes.swift:20-26, 117-118 | same |
| cleanupChoice | `local-rules`/`local-model`/`custom-api`/`murmur-cloud` | `local-rules` | Cleanup route | Routes.swift:37-44, 120 | same; `local-model` runs Vedu Scribe through `Murmur.Cleanup` |
| customAPIBaseURL | string | "" | Custom cleanup server | Routes.swift:121 | same |
| customTranscriptionAPIBaseURL | string | customAPIBaseURL when unset | Custom transcription server | Routes.swift:122, 187 | same |
| customAPIModel | string | "" | Custom cleanup model | Routes.swift:123 | same |
| customTranscriptionModel | string | "" | Custom transcription model | Routes.swift:124 | same |
| customAPIReasoningEffort | string | `low` ("" sends none) | `reasoning_effort` for custom cleanup | Routes.swift:125, 196 | same |
| customAPIKeyEndpoint.\<provider\> | string | none | Address a saved key is bound to | Routes.swift:213-233 | same |
| meetingTranscription | `local`/`cloud` | `local` | Meeting transcription route | Meetings.swift:145-146 | same |
| meetingNotesRoute | `none`/`apple-intelligence`/`custom-api` | unset: asks at first Stop | Meeting notes route | MeetingNotes.swift:45-48 | `none`/`custom-api` only |
| meetingConsentVersion | int | 0 | Recording consent acknowledged (version 1) | Meetings.swift:316-318 | same |
| meetingSpeakers | bool | false | Separate speakers after meetings | MeetingSpeakers.swift:290 | same |
| meetingRoomSpeakers | bool | false | Separate speakers in mic-only recordings | MeetingSpeakers.swift:290 | same |
| callDetectionEnabled | bool | true | Offer meeting notes on calls | CallDetection.swift:8, 10 | same |
| callDetectionNever | [string] | [] | Apps never offered (lowercased) | CallDetection.swift:9, 12-19 | process names |
| writingModes | JSON [WritingMode] | presets seeded on first load | Writing modes | WritingModes.swift:78, 86-93 | same JSON; app matchers use process names |
| writingModePresetsSeeded | bool | false | Presets added once | WritingModes.swift:80 | same |
| writingModeEditableBaselines | bool | false | Mac migration flag | WritingModes.swift:81 | not ported |
| writingModesUnreadable | data | none | Undecodable list kept aside | WritingModes.swift:83, 89 | same (never discard user data) |
| tips.enabled | bool | true | Show tips | Tips.swift:9 | same |
| tips.lastShown | date | none | Last floating tip (one per day) | Tips.swift:9, 23 | same |
| tips.dismissals | {string:int} | {} | Dismiss counts; two stops a tip | Tips.swift:9, 18 | same |
| tips.done | [string] | [] | Completed tips | Tips.swift:9, 19 | same |
| tips.lifetimeWords | int | absent until seeded | Lifetime words dictated | Tips.swift:10, 45 | same |
| tips.lifetimeSecondsSaved | double | absent until seeded | Lifetime seconds saved | Tips.swift:10, 46 | same |
| tips.milestoneRung | int | 0 | Highest milestone | Tips.swift:10, 48 | same |
| tips.homeDismissedRung | int | 0 | Home milestone dismissed at rung | Tips.swift:11, 58 | same |
| tips.rewrites | [date] | [] | Rewrite times, 7-day window | Tips.swift:11, 98-101 | same |
| tips.firstUse | date | none | First use | Tips.swift:11, 59 | same |
| tips.openedPanes | [int] | [] | Settings panes opened | Tips.swift:11, 125-130 | same |
| welcomeGuideStep | int | absent = welcome | Saved guide step | WelcomeGuide.swift:20, 37-43 | same; steps rewritten for Windows |
| welcomeGuideCompletedVersion | int | 0 | Guide version finished (current 1) | WelcomeGuide.swift:20, 46-57 | same, no migration |
| welcomeGuideReviewedChoices | [int] | [] | Reviewed guide steps | WelcomeGuide.swift:22, 25-29 | same |
| professionalHints | bool | true | Suggest Professional in History | ProfessionalHintView.swift:11, 25 | same |
| professionalHintState | JSON | empty | Suggestion counters | ProfessionalHintView.swift:11, 22 | same |
| SUEnableAutomaticChecks, Sparkle keys | Sparkle | Sparkle default | Sparkle's own state | AppUpdater.swift:34-36 | not ported |

## Credentials

Mac: Keychain generic passwords, service `Murmur`, account = provider ID, `WhenUnlockedThisDeviceOnly` (`Credentials.swift:114-149`).

Windows: Credential Manager generic credentials, target `Murmur/<account>`, persist `CRED_PERSIST_LOCAL_MACHINE` (stays on this PC; never `ENTERPRISE`, which roams).

| account | holds | Mac ref |
| --- | --- | --- |
| customTranscription | Custom transcription key | Credentials.swift:14 |
| customCleanup | Custom cleanup key | Credentials.swift:15 |
| murmur-cloud | Murmur Cloud key | Credentials.swift:22 |

Legacy accounts (`customAPI`, `openrouter`, `cerebras`, `localServer`) are not ported. Delete All removes every `Murmur/*` credential (`Credentials.swift:167-172`).

## Files

Mac root: `~/Library/Application Support/Dictation/` (`Retention.swift:217-222`), folders 0700, private files 0600 written atomically (`PrivateFiles.swift:5-27`, `History.swift:189-200`).

Windows root: `%LOCALAPPDATA%\Murmur\`, separate from the Velopack install folder `%LOCALAPPDATA%\MurmurApp\`; uninstall leaves it (PLAN "Packaging"). The root gets a protected DACL granting only the current user and SYSTEM; files inherit it. Atomic replace through a temp file in the same folder plus `File.Replace`/`File.Move(overwrite)`.

| path | contents | Mac ref | retention |
| --- | --- | --- | --- |
| history/\<uuid\>.json | One History entry | History.swift:189-200 | By `retention`; 50 MB cap warning (Retention.swift:75) |
| history/.pending-\<uuid\> | Temp before rename | History.swift:190-194 | Cleaned at open |
| history/unreadable/ | Damaged entries moved aside | History.swift:234-250 | Still expires with retention |
| history/meetings/\<uuid\>.json | Meeting transcript and notes | MeetingStore.swift:21-29 | Covered by retention and Delete All |
| dictionary/dictionary.json | Dictionary | Dictionary.swift:88, 122 | Never expires |
| dictionary/ledger.json | Learned corrections | Learning.swift:373, 449 | Never expires |
| dictionary/cues.json | Voice cues | VoiceCues.swift:108, 115 | Never expires |
| dictionary/parakeet-vocabulary.json | Dictionary words for the speech helper | Controller+Dictionary.swift:20-21 | Removed when no terms |
| retry-audio/\<uuid\>.wav | Dictation audio kept for retry | Capture.swift:431-455 | 24 h (Retention.swift:190-194) |
| retry-audio/meetings/\<id\>/ | Meeting chunk audio | MeetingRecorder.swift:158-159 | Removed when the meeting finishes |
| retry-audio/speakers/\<id\>.wav | Them audio pending speaker separation | MeetingSpeakers.swift:191-197 | 24 h at most (SpeakerModelSettings.swift:16) |
| chunks/ | Transient transcription chunks | Controller.swift:346-347 | Wiped at every launch |
| Models/ | Downloaded models | ModelText.swift:97-135 | Mac keeps these in a cache folder outside the root; on Windows they sit under the root but Delete All keeps them, as on the Mac (Retention.swift:262) |
| settings.json | Settings | (UserDefaults on the Mac) | Kept by Delete All except `dictionarySuggestionsDismissed` and `tips.*` |

`history/recordings/` (diagnostic WAVs, `Retention.swift:154-186`) is only written when `keepRecordings` is on, which never happens; not ported.

## Delete All and backup

- Delete All (`Retention.swift:242-256`, `Controller+History.swift:115-137`) removes history, dictionary, retry-audio, chunks, all credentials, `dictionarySuggestionsDismissed` and `tips.*`. Settings, routes, modes and models stay.
- Backup (`Retention.swift:314-390`) writes settings (without machine-specific keys), history and dictionary to a user-chosen folder, never keys or audio. Restore keeps this machine's values for the excluded keys. Windows writes `settings.json` instead of `settings.plist`, so a Mac backup does not restore on Windows in v1.

### Required: a restore never redirects dictations or touches files outside the data folder

From main `a4abc4c` (PR #96). M2 must implement all of it.

- **Destination settings are machine settings.** `DataBackup.isMachineSetting` (`Retention.swift:322`) also excludes `destinationSettings` (`Retention.swift:327`): `transcriptionRoute`, `cloudTranscriber`, `cleanupChoice`, `cleanupRoute`, `customAPIBaseURL`, `customTranscriptionAPIBaseURL`, `localServerURL`.
  - These join the existing exclusions: `modelDirectory`, `inputDeviceUID`, `customAPIKeyEndpoint*`, updater keys and window frames.
  - A restored backup, possibly from someone else, can never change where audio or text is sent.
  - `DataBackup.merged` (`Retention.swift:356`) keeps this machine's values for all of them, and drops the backup's value when this machine has none.
  - Windows uses the same key set, including the legacy names, so the lists stay identical.
- **Links inside a backup are removed before restore.** `DataBackup.removeLinks` (`Retention.swift:361`) walks the staged copy and deletes every link, and `restore` calls it after copying (`Retention.swift:381`).
  - On Windows a link means any reparse point (`FileAttributes.ReparsePoint`): symbolic links, junctions and mount points. A junction can redirect a folder just as a symlink can.
- **Purges never follow links** (`FileLinks.isLink`, `Retention.swift:191`). These purges skip a folder that is a link, and skip any entry that is a link:
  - the History unreadable purge (`History.swift:259-260`);
  - the meeting unreadable purge (`MeetingStore.swift:63-64`);
  - the diagnostic recordings purge (`Retention.swift:180`).
  
  The same rule applies on Windows to every purge, retention sweep and Delete All. Delete All removes a link itself, never what it points to. Nothing outside `%LOCALAPPDATA%\Murmur\` is ever deleted.
- **Check cases to port:**
  - `check_app_services.swift:233-250`:
    - "restore never changes where dictations are sent";
    - "restore drops links inside the backup";
    - "recording purge does not follow a linked folder".
  - `check_routes.swift:144-146`: "every destination setting stays on this Mac when restoring". This ties `destinationSettings` to the route keys, so a new route key can't be missed.
  - Windows adds a junction case next to each symlink case.
