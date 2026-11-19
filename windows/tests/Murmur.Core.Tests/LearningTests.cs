// Ports the learning checks of app/check_dictionary.swift (CorrectionLedger, CorrectionFilter.Replacements and the legacy ledger formats).
using Murmur.Core;
using static Murmur.Core.CandidateRejection;
using static Murmur.Core.CorrectionLedger.RecordKind;

namespace Murmur.Core.Tests;

public class LearningTests
{
    static readonly DateTime T0 = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000).UtcDateTime;

    static ObservedCorrection Obs(Guid session, string s, string t, double offset = 0) => new() { SessionId = session, Source = s, Target = t, ObservedAt = T0.AddSeconds(offset) };
    static (DictionaryStore Dict, CorrectionLedger Ledger) Fresh(string root, string name)
    {
        var d = Path.Combine(root, name);
        var dict = new DictionaryStore(d);
        return (dict, new CorrectionLedger(d, dict));
    }
    static List<DictionaryEntry> Learned(DictionaryStore dict) => dict.Entries.Where(e => e.Source == EntrySource.Learned).ToList();
    static string[] Prefs(IEnumerable<DictionaryEntry> entries) => entries.Select(e => e.Preferred).ToArray();
    static bool Is(CorrectionLedger.RecordOutcome o, CorrectionLedger.RecordKind kind, CandidateRejection? reason = null) => o.Kind == kind && o.Rejection == reason;
    static CorrectionLedger.Pair P(string s, string t) => new(s, t);
    static Guid G() => Guid.NewGuid();

    [Fact]
    public void OldProvisionalCandidateIsNotPromotedAfterTheGuardChanged()
    {
        var c = new Checker(); var root = TempDirs.New();
        var (dict, ledger) = Fresh(root, "old-provisional");
        var session = G();
        dict.Add("clouddev", []);
        c.Check(Is(ledger.Record(Obs(session, "cloud", "clouddev")), Provisional), "old candidate recorded");
        ledger.IsCommonWord = w => w == "cloud";
        c.Check(ledger.Settle(session).Count == 0 && Learned(dict).Count == 0, "old unsafe candidate is not promoted");
        c.Done();
    }

    [Fact]
    public void OneSettledFixLearnsAndReplaces()
    {
        var c = new Checker(); var root = TempDirs.New();
        var (dict, ledger) = Fresh(root, "promote");
        var a = G(); var b = G();
        c.Check(Is(ledger.Record(Obs(a, "Acme", "Akme")), Provisional), "first observation provisional");
        c.Check(dict.Apply("Ask Acme today") == "Ask Acme today", "provisional evidence replaces nothing");
        c.Check(Prefs(ledger.Settle(a)).SequenceEqual(["Akme"]), "one fix learns");
        c.Check(Learned(dict).Count == 1 && Learned(dict)[0].Variants.SequenceEqual(["Acme"]), "one fix adds the misheard form as a variant");
        c.Check(dict.VocabularyTerms.SequenceEqual(["Akme"]), "learned spelling reaches transcription vocabulary");
        c.Check(dict.Apply("Ask Acme today") == "Ask Akme today", "one fix replaces from the next dictation");
        c.Check(ledger.Evidence.Count == 0, "evidence consumed on promotion");
        c.Check(Is(ledger.Record(Obs(b, "acme", "AKME", 10)), Provisional), "repeat fix (case differs) records");
        c.Check(ledger.Settle(b).Count == 0, "repeat fix adds nothing");
        c.Check(Learned(dict).Count == 1 && Learned(dict)[0].Variants.SequenceEqual(["Acme"]) && ledger.Evidence.Count == 0, "repeat fix confirms without a duplicate or leftover evidence");
        // Ledger file holds only short strings.
        var path = Path.Combine(root, "promote", "ledger.json");
        c.Check(!File.ReadAllText(path).Contains("Ask"), "ledger has no document text");
        if (!OperatingSystem.IsWindows()) c.Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "ledger file is 0600");
        c.Done();
    }

    [Fact]
    public void TwoObservationsInTheSameSessionLearnOnce()
    {
        var c = new Checker(); var (dict, ledger) = Fresh(TempDirs.New(), "same-session");
        var a = G();
        ledger.Record(Obs(a, "Acme", "Akme"));
        ledger.Record(Obs(a, "Acme", "Akme", 5));
        ledger.Settle(a);
        c.Check(ledger.Settle(a).Count == 0, "settling twice is harmless");
        c.Check(Is(ledger.Record(Obs(a, "Acme", "Akme", 6)), SessionClosed), "closed session ignored");
        c.Check(Learned(dict).Count == 1 && Learned(dict)[0].Variants.SequenceEqual(["Acme"]) && ledger.Evidence.Count == 0, "same session counts once");
        c.Done();
    }

    [Fact]
    public void SettleOnStableTextThenKeepWatching()
    {
        var c = new Checker(); var root = TempDirs.New();
        var (dict, ledger) = Fresh(root, "early-settle");
        var a = G(); var b = G(); var cc = G();
        ledger.Record(Obs(a, "Acme", "Akme"));
        c.Check(Prefs(ledger.Settle(a)).SequenceEqual(["Akme"]), "early settle learns before the window closes");
        c.Check(dict.Apply("Ask Acme") == "Ask Akme", "early settle replaces");
        c.Check(Is(ledger.Record(Obs(a, "Acme", "Akme", 1)), SessionClosed), "same reading after an early settle changes nothing");
        c.Check(Is(ledger.Record(Obs(a, "Acme", "Akmee", 2)), Provisional), "a later edit re-records the session");
        c.Check(Learned(dict).Count == 0 && ledger.Evidence.Select(e => e.Target).SequenceEqual(["Akmee"]) && ledger.ProvisionalSessions.SetEquals([a]),
                "later edit withdraws the early replacement and replaces the evidence");
        c.Check(Prefs(ledger.Settle(a)).SequenceEqual(["Akmee"]) && ledger.Blocked.Count == 0, "settling the replacement learns it without a conflict");
        // Back to the first fix, settled again: learned once.
        ledger.Record(Obs(a, "Acme", "Akme", 3)); ledger.Settle(a);
        ledger.Settle(a);
        c.Check(Learned(dict).Count == 1 && Learned(dict)[0].Preferred == "Akme" && Learned(dict)[0].Variants.SequenceEqual(["Acme"]), "a session settled twice learns once");
        ledger.Record(Obs(b, "Acme", "Akme", 10)); ledger.Settle(b);
        c.Check(dict.Apply("Ask Acme") == "Ask Akme" && Learned(dict).Count == 1, "another session's same fix confirms it");
        // A second fix after an early settle keeps the first fix's replacement.
        var (dict3, ledger3) = Fresh(root, "early-second-fix");
        var d = G(); var e = G();
        ledger3.Record(Obs(d, "Acme", "Akme")); ledger3.Settle(d);
        ledger3.Record(Obs(e, "Acme met Zorblat", "Akme met Zorblat", 10)); ledger3.Settle(e);
        var owner = Learned(dict3)[0].Id;
        c.Check(Is(ledger3.Record(Obs(e, "Acme met Zorblat", "Akme met Zorblatt", 11)), Provisional), "second fix in the same span records");
        c.Check(dict3.Apply("Ask Acme") == "Ask Akme" && Learned(dict3)[0].Id == owner, "first fix's replacement survives a second fix");
        c.Check(Prefs(ledger3.Settle(e)).SequenceEqual(["Zorblatt"]), "second fix learns its own replacement");
        c.Check(dict3.Apply("Ask Acme met Zorblat") == "Ask Akme met Zorblatt", "both replacements apply after the final settle");
        // Undo after an early settle withdraws what it promoted.
        var (dict2, ledger2) = Fresh(root, "early-undo");
        ledger2.Record(Obs(cc, "Acme", "Akme")); ledger2.Settle(cc);
        ledger2.Revert(cc);
        c.Check(Learned(dict2).Count == 0 && ledger2.Evidence.Count == 0 && ledger2.Tombstones.Count == 0, "undo after an early settle withdraws the replacement");
        c.Done();
    }

    [Fact]
    public void NoticeUndoRemovesWhatItLearnedAndTombstonesIt()
    {
        var c = new Checker(); var root = TempDirs.New();
        var (dict, ledger) = Fresh(root, "notice-undo");
        var a = G(); var b = G();
        ledger.Record(Obs(a, "Akash", "Aakash")); ledger.Settle(a);
        ledger.Record(Obs(b, "Aakesh", "Aakash")); var added = ledger.Settle(b);
        var id = Learned(dict)[0].Id;
        c.Check(added.Select(e => e.Id).SequenceEqual([id]) && Learned(dict)[0].Variants.SequenceEqual(["Akash", "Aakesh"]), "a second mishearing joins the entry and is announced");
        ledger.UndoLearned(id);
        c.Check(Learned(dict).Select(e => e.Variants.ToArray()).Single().SequenceEqual(["Akash"]) && dict.Apply("Aakesh") == "Aakesh", "Undo removes only the announced variant");
        c.Check(ledger.Tombstones.Contains(P("aakesh", "aakash")), "Undo tombstones the variant");
        c.Check(Is(ledger.Record(Obs(G(), "Aakesh", "Aakash")), Tombstoned), "an undone fix is not learned again");
        ledger.UndoLearned(id);
        c.Check(Learned(dict).Count == 0 && ledger.Tombstones.Contains(P("akash", "aakash")), "Undo of a new entry deletes and tombstones it");
        ledger.UndoLearned(id);
        c.Check(Learned(dict).Count == 0, "a stale Undo does nothing");
        c.Done();
    }

    [Fact]
    public void UndoThatCannotBeSavedKeepsTheWord()
    {
        if (OperatingSystem.IsWindows()) return; // a read-only folder is a Unix mode check
        var c = new Checker(); var root = TempDirs.New();
        var (locked, lockedLedger) = Fresh(root, "undo-unwritable");
        var s = G();
        lockedLedger.Record(Obs(s, "Akash", "Aakash")); lockedLedger.Settle(s);
        var lockedId = Learned(locked)[0].Id;
        var folder = Path.Combine(root, "undo-unwritable");
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            bool enforced;
            try { File.WriteAllBytes(Path.Combine(folder, ".probe"), []); File.Delete(Path.Combine(folder, ".probe")); enforced = false; } catch (UnauthorizedAccessException) { enforced = true; }
            if (!enforced) return; // root ignores the mode: nothing to observe
            bool failed = false;
            try { lockedLedger.UndoLearned(lockedId); } catch (UnauthorizedAccessException) { failed = true; }
            c.Check(failed && Learned(locked).Select(e => e.Id).SequenceEqual([lockedId]), "an Undo that cannot be saved fails and keeps the word");
        }
        finally { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        lockedLedger.UndoLearned(lockedId);
        c.Check(Learned(locked).Count == 0 && lockedLedger.Tombstones.Contains(P("akash", "aakash")), "the same Undo works once the dictionary can be saved");
        c.Done();
    }

    [Fact]
    public void HistoryEditLearnsThroughTheSameFilter()
    {
        var c = new Checker(); var (dict, ledger) = Fresh(TempDirs.New(), "history-edit");
        c.Check(Prefs(ledger.LearnFromEdit("I talked to Acme today.", "I talked to Akme today.")).SequenceEqual(["Akme"]), "history edit learns");
        c.Check(dict.Apply("Ask Acme") == "Ask Akme", "a history edit replaces in one step");
        c.Check(ledger.LearnFromEdit("I accept the terms.", "I decline the terms.").Count == 0 && ledger.Evidence.Count == 0 && Learned(dict).Count == 1, "history meaning edit rejected by the filter");
        c.Check(ledger.LearnFromEdit("Same text.", "Same text.").Count == 0, "unchanged history text learns nothing");
        ledger.LearnFromEdit("Acme shipped it.", "Akmee shipped it.");
        c.Check(Learned(dict).Count == 0 && ledger.SuppressedSources.Contains("acme"), "a conflicting history edit suppresses");
        ledger.Unblock(new CorrectionLedger.BlockedItem.Heard("acme"));
        ledger.LearnFromEdit("Acme shipped it.", "Akme shipped it.");
        ledger.DeleteLearned(Learned(dict)[0].Id);
        c.Check(ledger.LearnFromEdit("Acme again", "Akme again").Count == 0 && Learned(dict).Count == 0, "history edit honours tombstones");
        ledger.SetLearningEnabled(false);
        c.Check(ledger.LearnFromEdit("Zorblat", "Zorblatt").Count == 0 && ledger.Evidence.Count == 0, "history edit learns nothing with learning off");
        c.Done();
    }

    [Fact]
    public void RejectionsAreNeverStoredOrPromoted()
    {
        var c = new Checker(); var root = TempDirs.New();
        var (dict, ledger) = Fresh(root, "antonym");
        for (int i = 0; i < 3; i++)
        {
            var s = G();
            c.Check(Is(ledger.Record(Obs(s, "I accept", "I decline", i)), Rejected, Antonym), "accept->decline rejected");
            ledger.Settle(s);
        }
        c.Check(Learned(dict).Count == 0 && ledger.Evidence.Count == 0, "antonym never stored or promoted");
        var (_, caseLedger) = Fresh(root, "case");
        c.Check(Is(caseLedger.Record(Obs(G(), "acme", "Acme")), Rejected, CaseOnly), "case-only rejected");
        c.Check(Is(caseLedger.Record(Obs(G(), "Acme", "Acme.")), Rejected, PunctuationOnly), "punctuation-only rejected");
        c.Check(caseLedger.Evidence.Count == 0, "nothing stored");
        var (_, replaceLedger) = Fresh(root, "replace");
        var a = G();
        replaceLedger.Record(Obs(a, "Acme", "Akme"));
        c.Check(Is(replaceLedger.Record(Obs(a, "Acme", "Acme!")), Rejected, PunctuationOnly), "rejected snapshot");
        c.Check(replaceLedger.Evidence.Count == 0, "rejected snapshot clears session evidence");
        c.Done();
    }

    [Fact]
    public void ConflictsSuppressAndWithdraw()
    {
        var c = new Checker(); var root = TempDirs.New();
        var (dict, ledger) = Fresh(root, "conflict");
        var a = G(); var b = G(); var cc = G();
        ledger.Record(Obs(a, "Acme", "Akme")); ledger.Settle(a);
        c.Check(dict.Apply("Acme") == "Akme", "first fix replaces");
        ledger.Record(Obs(b, "Acme", "Acmee")); ledger.Settle(b);
        c.Check(ledger.SuppressedSources.Contains("acme") && Learned(dict).Count == 0 && dict.Apply("Acme") == "Acme", "a conflicting second fix suppresses and withdraws the replacement");
        c.Check(ledger.Tombstones.Count == 0, "a conflict does not tombstone");
        c.Check(Is(ledger.Record(Obs(cc, "Acme", "Akme")), Suppressed), "suppressed source not recorded");
        ledger.Settle(cc);
        c.Check(Learned(dict).Count == 0, "no promotion while suppressed");
        ledger.Reset();
        c.Check(ledger.SuppressedSources.Count == 0, "reset clears suppression");
        var d = G();
        ledger.Record(Obs(d, "Acme", "Akme")); ledger.Settle(d);
        c.Check(Learned(dict).Count == 1, "promotion possible again after reset");

        var (dict2, ledger2) = Fresh(root, "late-conflict");
        foreach (var target in new[] { "Akme", "Akme", "Acmee" }) { var s = G(); ledger2.Record(Obs(s, "Acme", target)); ledger2.Settle(s); }
        c.Check(Learned(dict2).Count == 0 && ledger2.SuppressedSources.Contains("acme"), "late conflict demotes and suppresses");
        c.Done();
    }

    [Fact]
    public void RevertRemovesEvidenceAndWithdrawsThePromotion()
    {
        var c = new Checker(); var (dict, ledger) = Fresh(TempDirs.New(), "revert");
        var a = G(); var b = G();
        ledger.Record(Obs(a, "Acme", "Akme"));
        ledger.Revert(a);
        c.Check(ledger.Evidence.Count == 0, "revert removes provisional evidence");
        ledger.Settle(a);
        c.Check(Learned(dict).Count == 0, "reverted evidence does not count");
        ledger.Record(Obs(b, "Acme", "Akme")); ledger.Settle(b);
        c.Check(Learned(dict).Count == 1, "promoted by b");
        ledger.Revert(b);
        c.Check(Learned(dict).Count == 0 && dict.Apply("Acme") == "Acme", "revert after promotion withdraws the replacement");
        c.Check(ledger.Tombstones.Count == 0, "revert does not tombstone");
        c.Done();
    }

    [Fact]
    public void DeletedLearnedEntryIsNotRePromoted()
    {
        var c = new Checker(); var (dict, ledger) = Fresh(TempDirs.New(), "delete");
        var a = G(); var b = G(); var cc = G();
        ledger.Record(Obs(a, "Acme", "Akme")); ledger.Settle(a);
        ledger.Record(Obs(b, "Acme", "Akme")); ledger.Settle(b);
        // Unsettled third observation in flight while the user deletes the entry.
        ledger.Record(Obs(cc, "Acme", "Akme"));
        var entry = Learned(dict)[0];
        ledger.DeleteLearned(entry.Id);
        c.Check(dict.Apply("Acme") == "Acme", "deleted learned entry stops applying");
        c.Check(ledger.Settle(cc).Count == 0 && Learned(dict).Count == 0, "not re-promoted from old evidence");
        c.Check(Is(ledger.Record(Obs(G(), "Acme", "Akme")), Tombstoned), "tombstoned pair not recorded");
        // Deleting through the dictionary directly is detected too.
        var x = G(); var y = G();
        ledger.Record(Obs(x, "Zorblat", "Zorblatt")); ledger.Settle(x);
        ledger.Record(Obs(y, "Zorblat", "Zorblatt")); ledger.Settle(y);
        var zor = Learned(dict).FirstOrDefault(e => e.Preferred == "Zorblatt");
        c.Check(zor is not null, "second learned entry");
        if (zor is not null) dict.Delete(zor.Id);
        c.Check(Is(ledger.Record(Obs(G(), "Zorblat", "Zorblatt")), Tombstoned), "direct dictionary delete tombstones");
        c.Check(Throws(() => ledger.DeleteLearned(Guid.NewGuid())), "deleting a missing entry fails");
        c.Done();
    }

    static bool Throws(Action action)
    {
        try { action(); return false; } catch (StoreError) { return true; }
    }

    [Fact]
    public void UserRevertingALearnedReplacementWithdrawsAndTombstonesIt()
    {
        var c = new Checker(); var (dict, ledger) = Fresh(TempDirs.New(), "reverse");
        for (int i = 0; i < 2; i++) { var s = G(); ledger.Record(Obs(s, "Acme", "Akme")); ledger.Settle(s); }
        c.Check(Learned(dict).Count == 1, "promoted before reversal");
        var r = G();
        ledger.Record(Obs(r, "Akme", "Acme")); ledger.Settle(r);
        c.Check(Learned(dict).Count == 0 && ledger.Tombstones.Contains(P("acme", "akme")), "reversal withdraws and tombstones");
        c.Done();
    }

    [Fact]
    public void WordDiffSeparatesFixesTypingRewritesAndDeletions()
    {
        var c = new Checker();
        static CorrectionFilter.Change Ch(string source, params string[] targets) => new(source, targets);
        static bool Same(CorrectionFilter.ReplacementsResult r, params CorrectionFilter.Change[] expected) => r.Changes is { } changes && changes.SequenceEqual(expected);
        c.Check(Same(CorrectionFilter.Replacements("call ACME about Gira", "call Akme about Jira"), Ch("ACME", "Akme"), Ch("Gira", "Jira")), "two fixes are two changes");
        c.Check(Same(CorrectionFilter.Replacements("pay Acme", "pay Akme and more"), Ch("Acme", "Akme", "Akme and", "Akme and more")), "fix then typing: shortest reading first");
        c.Check(Same(CorrectionFilter.Replacements("call Acme today", "call Akme today and more"), Ch("Acme", "Akme")), "words added after the span are ignored");
        c.Check(CorrectionFilter.Replacements("call Acme today", "call today").Failure == Deletion, "deletion");
        c.Check(CorrectionFilter.Replacements("maybe the team wants a new plan", "I think we should ship old stuff").Failure == TooManyWords, "rewrite");
        c.Check(Same(CorrectionFilter.Replacements("acme", "Acme"), Ch("acme", "Acme")), "a case change is a change");
        c.Done();
    }

    [Fact]
    public void CaseFixIsLearnedFromOneFixAndItsReversalWithdrawsIt()
    {
        var c = new Checker(); var (dict, ledger) = Fresh(TempDirs.New(), "case-tiers");
        ledger.IsCommonWord = w => w is "call" or "the";
        var first = G(); ledger.Record(Obs(first, "call jira", "call Jira")); ledger.Settle(first);
        c.Check(Prefs(Learned(dict)).SequenceEqual(["Jira"]) && Learned(dict)[0].Variants.SequenceEqual(["jira"]), "case fix: one fix adds the variant");
        c.Check(dict.Apply("open jira now") == "open Jira now", "case fix applies");
        var r = G(); ledger.Record(Obs(r, "Jira", "jira")); ledger.Settle(r);
        c.Check(Learned(dict).Count == 0, "lowercasing it again withdraws it");
        c.Check(ledger.Blocked.SequenceEqual([new CorrectionLedger.BlockedItem.Heard("jira"), new CorrectionLedger.BlockedItem.Spelling("Jira")]), "reversal blocks the heard form and the spelling");
        ledger.Unblock(new CorrectionLedger.BlockedItem.Spelling("jira")); ledger.Unblock(new CorrectionLedger.BlockedItem.Heard("jira"));
        c.Check(ledger.Blocked.Count == 0, "unblocked");
        c.Check(Is(ledger.Record(Obs(G(), "call jira", "call Jira")), Provisional), "learnable again after unblocking");
        c.Done();
    }

    [Fact]
    public void TwoMishearingsShareOneEntryAndADeletedWordIsNeverRelearned()
    {
        var c = new Checker(); var (dict, ledger) = Fresh(TempDirs.New(), "tiers");
        foreach (var source in new[] { "Akash", "Akash", "Aakesh", "Aakesh" }) { var s = G(); ledger.Record(Obs(s, source, "Aakash")); ledger.Settle(s); }
        c.Check(Learned(dict).Count == 1 && Learned(dict)[0].Variants.SequenceEqual(["Akash", "Aakesh"]), "one entry gathers both mishearings");
        ledger.DeleteLearned(Learned(dict)[0].Id);
        c.Check(Is(ledger.Record(Obs(G(), "Akesh", "Aakash")), Tombstoned), "deleted word not re-learned from a new mishearing");
        c.Done();
    }

    [Fact]
    public void LegacyLedgerFormatsStillLoad()
    {
        var c = new Checker(); var root = TempDirs.New();
        // A version-1 ledger written before tiers (promotion without kind) still withdraws its entry.
        {
            var d = Path.Combine(root, "legacy");
            var dict = new DictionaryStore(d);
            var entry = dict.Add("Akme", ["Acme"], EntrySource.Learned);
            var legacy = $$"""{"evidence":[],"learningEnabled":true,"promotions":[{"entryID":"{{entry.Id.ToString().ToUpperInvariant()}}","pair":{"source":"acme","target":"akme"},"sessions":[]}],"suppressed":[],"tombstones":[],"version":1}""";
            PrivateFiles.Write(System.Text.Encoding.UTF8.GetBytes(legacy), Path.Combine(d, CorrectionLedger.FileName));
            var ledger = new CorrectionLedger(d, dict);
            var r = G(); ledger.Record(Obs(r, "Akme", "Acme")); ledger.Settle(r);
            c.Check(Learned(dict).Count == 0, "legacy promotion decodes and reversal withdraws it");
        }
        // Migration: an old first-tier hint and its settled evidence are not promoted at load or by an
        // unrelated settle; the next fix of that pair adds the variant, and Undo withdraws only that.
        {
            var d = Path.Combine(root, "legacy-hint");
            var dict = new DictionaryStore(d);
            var entry = dict.Add("Akme", [], EntrySource.Learned);
            var old = G();
            var legacy = $$"""{"evidence":[{"correction":{"observedAt":0,"sessionID":"{{old.ToString().ToUpperInvariant()}}","source":"Acme","target":"Akme"},"settled":true}],"learningEnabled":true,"promotions":[{"entryID":"{{entry.Id.ToString().ToUpperInvariant()}}","kind":"hint","pair":{"source":"acme","target":"akme"},"sessions":["{{old.ToString().ToUpperInvariant()}}"]}],"suppressed":[],"tombstones":[],"version":1}""";
            PrivateFiles.Write(System.Text.Encoding.UTF8.GetBytes(legacy), Path.Combine(d, CorrectionLedger.FileName));
            var ledger = new CorrectionLedger(d, dict);
            c.Check(ledger.Evidence.Count == 1 && Learned(dict)[0].Variants.Count == 0 && dict.Apply("Ask Acme") == "Ask Acme", "old hint not promoted at load");
            var x = G(); ledger.Record(Obs(x, "Zorblat", "Zorblatt")); ledger.Settle(x);
            c.Check(dict.Apply("Ask Acme") == "Ask Acme", "an unrelated settle does not promote old evidence");
            var y = G(); ledger.Record(Obs(y, "Acme", "Akme")); var upgraded = ledger.Settle(y);
            c.Check(upgraded.Select(e => e.Id).SequenceEqual([entry.Id]) && dict.Apply("Ask Acme") == "Ask Akme" && ledger.Evidence.All(e => e.Target != "Akme"), "the next fix upgrades the old hint and is announced");
            ledger.UndoLearned(entry.Id);
            c.Check(Learned(dict).Any(e => e.Variants.Count == 0) && dict.Apply("Ask Acme") == "Ask Acme" && ledger.Tombstones.Contains(P("acme", "akme")), "Undo of the upgrade removes and tombstones the variant");
        }
        c.Done();
    }

    [Fact]
    public void ManualOwnerLexiconAndGarbledNames()
    {
        var c = new Checker(); var root = TempDirs.New();
        {
            var (dict, ledger) = Fresh(root, "manual");
            dict.Add("Akme", ["Acme"]);
            var s = G(); ledger.Record(Obs(s, "Acme", "Akme")); ledger.Settle(s);
            c.Check(Learned(dict).Count == 0 && ledger.Evidence.Count == 0, "manual entry not duplicated");
        }
        {
            var (dict, ledger) = Fresh(root, "lexicon");
            ledger.IsCommonWord = w => w is "lunch" or "launch";
            var s = G();
            c.Check(Is(ledger.Record(Obs(s, "lunch", "launch")), Rejected, OrdinaryWords), "ledger uses lexicon");
            c.Check(ledger.Settle(s).Count == 0 && Learned(dict).Count == 0, "ordinary-word swap rejected under one-fix learning");
        }
        {
            var (dict, ledger) = Fresh(root, "garbled");
            var set = new HashSet<string> { "errors", "across", "the", "or", "codex", "app", "latest", "version" };
            ledger.IsCommonWord = set.Contains;
            var s = G();
            ledger.Record(Obs(s, "errors across the ChadJuT or Codex app", "errors across the ChatGPT or Codex app"));
            c.Check(Prefs(ledger.Settle(s)).SequenceEqual(["ChatGPT"]), "ChadJuT fix learned from one edit");
            c.Check(dict.Apply("the ChadJuT app", ledger.IsCommonWord) == "the ChatGPT app", "learned form replaced");
        }
        c.Done();
    }

    [Fact]
    public void LearningOffRecordsNothingAndPersists()
    {
        var c = new Checker(); var root = TempDirs.New();
        var (dict, ledger) = Fresh(root, "off");
        var a = G();
        ledger.Record(Obs(a, "Acme", "Akme"));
        ledger.SetLearningEnabled(false);
        c.Check(ledger.Evidence.Count == 0, "disabling drops provisional evidence");
        for (int i = 0; i < 3; i++)
        {
            var s = G();
            c.Check(Is(ledger.Record(Obs(s, "Acme", "Akme")), LearningDisabled), "off records nothing");
            ledger.Settle(s);
        }
        c.Check(ledger.Evidence.Count == 0 && Learned(dict).Count == 0, "nothing stored or promoted while off");
        var reopened = new CorrectionLedger(Path.Combine(root, "off"), dict);
        c.Check(!reopened.LearningEnabled, "toggle persists");
        c.Done();
    }

    [Fact]
    public void PromotionAndEvidenceSurviveARestart()
    {
        var c = new Checker(); var root = TempDirs.New();
        var d = Path.Combine(root, "persist");
        var a = G(); var b = G(); var cc = G();
        {
            var dict0 = new DictionaryStore(d);
            var ledger0 = new CorrectionLedger(d, dict0);
            ledger0.Record(Obs(a, "Acme", "Akme")); ledger0.Settle(a);
            ledger0.Record(Obs(cc, "Zorblat", "Zorblatt")); // provisional survives too
        }
        var dict = new DictionaryStore(d);
        var ledger = new CorrectionLedger(d, dict);
        c.Check(ledger.Evidence.Count == 1 && ledger.ProvisionalSessions.SetEquals([cc]) && Learned(dict).Count == 1, "ledger round-trip");
        ledger.Record(Obs(b, "Acme", "Akme")); ledger.Settle(b);
        c.Check(Learned(dict).Count == 1, "a repeat fix after restart confirms");
        var again = new CorrectionLedger(d, new DictionaryStore(d));
        again.Revert(a);
        c.Check(new DictionaryStore(d).Entries.Count(e => e.Source == EntrySource.Learned) == 0, "promotion sessions persist for revert");
        c.Done();
    }
}
