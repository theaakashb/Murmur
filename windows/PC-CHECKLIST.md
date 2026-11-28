# PC checklist: the M4 and M5 exits

Short runs that only a person on a real PC can do. Install and model setup are in `INSTALL-TEST.md` sections 1–3. Use the newest build the coordinator names.

Write results as pass/fail plus a few words. Never paste dictated text, History, transcripts or screenshots into results. Describe what went wrong instead.

## 1. Browser matrix: modes by website (about 20 min)

Setup: Settings > Writing > Modes. Add a mode named `Site test` matched to the website `github.com`. Any instructions will do (for example "write in capitals").

In each of **Chrome, Edge, Firefox and Brave**, open `github.com`, click into a text box (the search box is fine), hold Right Ctrl and say a short sentence. The mode **matched** if the text follows the mode's instructions.

| Case | Chrome | Edge | Firefox | Brave |
| --- | --- | --- | --- | --- |
| Normal window | | | | |
| Private window | | | | |
| Second window | | | | |
| After switching tabs (start on another site, switch to github.com) | | | | |
| F11 full screen | | | | |
| Address bar focused (dictate into the address bar) | | | | |
| Another site (should **not** match) | | | | |

Firefox is the least tested: it reads the address bar, and that reader has never been checked on a real install.

## 2. Learning on real fields (about 10 min)

Setup: Settings > Writing > Learning > "Learn from my corrections" on.

1. **Readable field (Notepad):** dictate a sentence that includes an unusual name. Within a minute, fix the name's spelling by hand. A notice should say "Added <name> to your dictionary", and the name should appear in Settings > Writing > Dictionary.
2. **Unreadable field:** repeat in an app whose text Windows accessibility can't read. Try Slack, Discord or Teams first and say which one you used. With "Learn only from readable fields" **off**, the fix should still be learned. With it **on**, nothing should be learned.
3. **Password field:** dictate into any password box (for example a browser sign-in form). Nothing should be learned there.

## 3. Voice edit with a real provider (about 5 min)

Setup: a working cleanup provider: a Custom Provider with its key.

1. In Notepad, dictate a sentence, then select it.
2. Hold Right Ctrl and say an edit, such as "make this more formal". The selection should be replaced by the edited text.
3. With nothing selected, Right Ctrl should dictate as usual.
4. Note the time from releasing the key to the new text appearing.

Using the same key with a selection is the default, and that's what this run tests. Optional: Settings > Dictation > Recording > Voice edit shortcut > pick a separate key, then repeat step 2 with that key.

## 4. A real model download (about 5 min)

Settings > Models > download the speech model, or the cleanup model if the speech model is already installed. While it downloads, move the Settings window, switch tabs and open History. The app must stay responsive, and Cancel must work.

## 5. A 30-minute meeting

Follow `INSTALL-TEST.md` "Meetings: a real 30-minute call (M5 exit)". It passes when Me and Them stay aligned for the full 30 minutes, the meeting recovers after the kill at about minute 15, and the notes (or the transcript alone) are saved. Add the speaker separation run if two or more other people are on the call.

## Report back

For each section give pass/fail and a few words, plus the build you used. The coordinator updates `PLAN.md` Status.
