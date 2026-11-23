// Ports the dictionary, filter and suggestion checks of app/check_dictionary.swift (the retention checks belong to Retention.cs, the learning ones to LearningTests.cs).
using Murmur.Core;
using static Murmur.Core.CandidateRejection;

namespace Murmur.Core.Tests;

public class DictionaryTests
{
    static CandidateVerdict Accepted(string s, string t) => new CandidateVerdict.Accepted(s, t);
    static CandidateVerdict Rejected(CandidateRejection r) => new CandidateVerdict.Rejected(r);
    static Func<string, bool> Lexicon(params string[] words) { var set = words.ToHashSet(); return set.Contains; }

    static DictionaryEntry Learned(DictionaryStore d, string preferred) => d.Entries.First(e => e.Preferred == preferred);

    [Fact]
    public void CommandPhrasesMatchTheRuleCleanerCommands()
    {
        var derived = RuleCleaner.MarkCommands.Select(c => string.Join(" ", c.Words)).Concat(RuleCleaner.AtPhrases.Select(p => string.Join(" ", p))).ToHashSet();
        Assert.True(derived.SetEquals(DictionaryStore.CommandPhrases));
    }

    [Fact]
    public void DictionaryChecks()
    {
        var c = new Checker();
        var dir = TempDirs.New("dict");
        var store = new DictionaryStore(dir);
        var acme = store.Add("Akme", ["Acme", "ack me"]);
        store.Add("New York City", ["new york city", "nyc"]);
        store.Add("New Yorker", ["new york"]);
        store.Add("kubectl", ["cube cuddle"]);
        store.Add("iPhone", ["i phone"]);

        // Word boundaries: never inside other words, hyphenated or possessive forms.
        c.Check(store.Apply("Acme and Acmeville") == "Akme and Acmeville", "Acme not inside Acmeville");
        c.Check(store.Apply("Acme's plan, Acme-based, MyAcme, acme.") == "Acme's plan, Acme-based, MyAcme, Akme.", "boundaries");
        c.Check(store.Apply("call ACME now") == "call Akme now", "case-insensitive match, preferred as stored");
        c.Check(store.Apply("please ack me later") == "please Akme later", "multiword variant");
        c.Check(store.Apply("ack\nme") == "ack\nme", "line break ends a phrase");
        c.Check(store.Apply("ack, me") == "ack, me", "punctuation ends a phrase");
        // Longest match wins.
        c.Check(store.Apply("I love new york city.") == "I love New York City.", "longest match");
        c.Check(store.Apply("I love new york.") == "I love New Yorker.", "shorter phrase when longer absent");
        // Case policy.
        c.Check(store.Apply("Cube cuddle is installed. cube cuddle again") == "Kubectl is installed. kubectl again", "sentence-initial capitalization of lowercase preferred");
        c.Check(store.Apply("I phone you. My i phone") == "iPhone you. My iPhone", "mixed-case preferred never altered");
        // No fuzzy matching.
        c.Check(store.Apply("Acne and Akmee and acmes") == "Acne and Akmee and acmes", "similar words untouched");
        c.Check(store.Apply("café ☕ Acme 🙂") == "café ☕ Akme 🙂", "unicode neighbours");
        c.Check(store.Apply("") == "", "empty text");
        var multilingual = new DictionaryStore(Path.Combine(dir, "multilingual"));
        multilingual.Add("Akme", ["Acme"]);
        multilingual.Add("WidgetCo", ["widget company"], EntrySource.Learned);
        c.Check(multilingual.Apply("नमस्ते Acme widget company مرحبًا", manualVariantsOnly: true) == "नमस्ते Akme widget company مرحبًا",
            "multilingual correction preserves scripts and skips learned variants");

        // An entry whose target is a spoken command applies first and is never a hint.
        {
            var commands = new DictionaryStore(Path.Combine(dir, "commands"));
            var command = commands.Add("question mark", ["Kocia Mark"]);
            commands.Add("Marc", ["mark"]);
            commands.Add("Kocia Inc", ["kocia mark please"]);
            c.Check(commands.Apply("Is it done kocia mark") == "Is it done question mark", "misheard command replaced");
            c.Check(commands.Apply("Kocia Mark", manualVariantsOnly: true) != "question mark", "multilingual correction skips spoken commands");
            c.Check(commands.Apply("Kocia Mark") == "question mark", "command target keeps its form at a sentence start");
            c.Check(commands.Apply("kocia mark please") == "question mark please", "command target wins over a longer entry");
            c.Check(commands.Apply("ask mark") == "ask Marc", "other entries still apply");
            c.Check(commands.VocabularyTerms.SequenceEqual(["Marc", "Kocia Inc"]), "command target excluded from transcription vocabulary");
            c.Check(!commands.RelevantEntries("kocia mark").Any(e => e.Id == command.Id) && !commands.RelevantEntries("question mark").Any(e => e.Id == command.Id), "command target excluded from cleanup hints");
        }

        // relevantEntries minimality.
        c.Check(store.RelevantEntries("Ask Acme about it").Select(e => e.Id).SequenceEqual([acme.Id]), "only the matching entry is relevant");
        c.Check(store.RelevantEntries("nothing here").Count == 0, "no entries for unrelated text");
        c.Check(store.RelevantEntries("Akme already right").Select(e => e.Id).SequenceEqual([acme.Id]), "preferred spelling counts");
        c.Check(store.RelevantEntries("Acmeville").Count == 0, "relevance uses word boundaries");
        // Near matches feed the cleanup hint only; Apply never uses them.
        var kube = store.Add("Kubernetes", []);
        var cloud = store.Add("clouddev", []);
        c.Check(store.RelevantEntries("deploy it to coopernetties").Select(e => e.Id).SequenceEqual([kube.Id]), "misheard single word is near");
        c.Check(store.RelevantEntries("ship cloud dev today").Select(e => e.Id).SequenceEqual([cloud.Id]), "split words spelling the term are near");
        c.Check(store.Apply("deploy it to coopernetties") == "deploy it to coopernetties", "near match never applied");
        c.Check(store.RelevantEntries("Can we meet for lunch tomorrow at the office?").Count == 0, "ordinary sentence pulls in no terms");
        store.Update(kube with { Enabled = false });
        c.Check(store.RelevantEntries("deploy it to coopernetties").Count == 0, "disabled entry is never near");
        store.Delete(kube.Id); store.Delete(cloud.Id);
        c.Check(store.VocabularyTerms.SequenceEqual(["Akme", "New York City", "New Yorker", "kubectl", "iPhone"]), "vocabulary terms: enabled preferred spellings in order");

        // Validation, edit, disable, delete.
        c.Check(Throws(() => store.Add("Other", ["acme"])), "duplicate variant rejected");
        c.Check(Throws(() => store.Add("X", ["a,b"])), "punctuated variant rejected");
        var edited = acme with { Enabled = false };
        store.Update(edited);
        c.Check(store.Apply("Acme") == "Acme", "disabled entry not applied");
        c.Check(store.RelevantEntries("Acme").Count == 0, "disabled entry not relevant");
        edited = edited with { Enabled = true, Variants = ["acme corp"] };
        store.Update(edited);
        c.Check(store.Apply("Acme Corp and Acme") == "Akme and Acme", "edited variants");

        // Persistence and permissions.
        var reloaded = new DictionaryStore(dir);
        c.Check(reloaded.Entries.SequenceEqual(store.Entries), "dictionary round-trip");
        if (!OperatingSystem.IsWindows())
        {
            c.Check(File.GetUnixFileMode(Path.Combine(dir, "dictionary.json")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "dictionary file is 0600");
            c.Check(File.GetUnixFileMode(dir) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "dictionary directory is 0700");
        }
        store.Delete(acme.Id);
        c.Check(store.Apply("Acme Corp") == "Acme Corp", "deleted entry not applied");

        // resetLearned keeps manual entries.
        store.Add("Zylo", ["xylo"], EntrySource.Learned);
        int manualCount = store.Entries.Count(e => e.Source == EntrySource.Manual);
        var removed = store.ResetLearned();
        c.Check(removed.Count == 1 && store.Entries.All(e => e.Source == EntrySource.Manual) && store.Entries.Count == manualCount, "resetLearned removes only learned");
        c.Done();
    }

    static bool Throws(Action action)
    {
        try { action(); return false; } catch (StoreError) { return true; }
    }

    [Fact]
    public void SuggestionChecks()
    {
        var c = new Checker();
        // Real misheard forms from History: Whisper spells the same name several ways; cleanup wrote it right.
        var common = new HashSet<string> { "we", "use", "for", "the", "tokens", "ask", "about", "send", "to", "today", "call", "and", "fast", "is", "very" };
        Func<string, bool> isCommon = w => common.Contains(SwiftText.Lowercased(w));
        var said = new List<(string Raw, string Final)>
        {
            ("we use Serbrass for the tokens", "We use Cerebras for the tokens."), ("ask Serebrass about tokens", "Ask Cerebras about tokens."),
            ("Serobrass is very fast", "Serobrass is very fast."), ("send Buzari's tokens today", "Send Buzzati's tokens today."),
            ("call Buzati today", "Call Buzati today."), ("send Proxmox tokens", "Send Proxmox tokens."), ("call Akme and Akme", "Call Akme and Akme."),
        };
        var akme = new DictionaryEntry { Id = Guid.NewGuid(), Preferred = "Akme", Variants = [], Source = EntrySource.Manual, CreatedAt = DateTime.UtcNow, Enabled = true };
        var found = DictionarySuggestions.Find(said, [akme], new HashSet<string>(), isCommon);
        c.Check(found.Count > 0 && found[0] == new DictionarySuggestion("Cerebras", ["Serbrass", "Serebrass", "Serobrass"], 3), "spellings group; cleanup's spelling is preferred");
        c.Check(found.Any(f => f.Heard.SequenceEqual(["Buzari", "Buzati"]) && f.Preferred == "Buzzati"), "possessive stripped, grouped with its plain form");
        c.Check(!found.Any(f => f.Heard.Contains("Proxmox")) && !found.Any(f => f.Heard.Contains("Akme")), "one spelling in one dictation, or a known entry, is not suggested");
        c.Check(DictionarySuggestions.Find(said, [], new HashSet<string> { "serbrass", "serebrass", "serobrass" }, isCommon).All(f => !f.Heard.Contains("Serbrass")), "dismissed spellings stay away");
        var more = said.Concat(Enumerable.Repeat(("ask Proxmox", "Ask Proxmox."), 3)).ToList();
        c.Check(DictionarySuggestions.Find(more, [], new HashSet<string>(), isCommon).Any(f => f.Heard.SequenceEqual(["Proxmox"]) && f.Dictations == 4), "one spelling recurring in four dictations is suggested");
        c.Done();
    }

    [Fact]
    public void FilterChecks()
    {
        var c = new Checker();
        CandidateVerdict Verdict(string s, string t, params string[] manual) => CorrectionFilter.Evaluate(s, t, manual.ToHashSet());
        c.Check(Verdict("Acme", "Akme") == Accepted("Acme", "Akme"), "Acme -> Akme accepted");
        c.Check(Verdict("I accept", "I decline") == Rejected(Antonym), "accept -> decline rejected");
        c.Check(Verdict("accept", "decline") == Rejected(Antonym), "bare accept -> decline rejected");
        c.Check(Verdict("Acme", "acme") == Rejected(CaseOnly), "case-only");
        c.Check(Verdict("Acme", "Acme!") == Rejected(PunctuationOnly), "punctuation-only");
        c.Check(Verdict("e mail", "e-mail") == Rejected(PunctuationOnly), "spacing/hyphen only");
        c.Check(Verdict("Acme", "") == Rejected(Deletion), "deletion");
        c.Check(Verdict("Acme Corp", "Acme") == Rejected(Deletion), "word deletion");
        c.Check(Verdict("Acme", "Acme Corp") == Rejected(Insertion), "append");
        c.Check(Verdict("Acme", "big Acme") == Rejected(Insertion), "prefix insertion");
        c.Check(Verdict("one two three", "four five six") == Rejected(Number), "number words");
        c.Check(Verdict("alpha beta gamma delta", "alpho betta gamme delto") == Rejected(TooManyWords), "broad rewrite");
        c.Check(Verdict("will go", "will not go") == Rejected(Insertion), "inserted negation is an insertion");
        c.Check(Verdict("is able", "is unable") == Rejected(Negation), "negating prefix");
        c.Check(Verdict("should go", "shouldn't go") == Rejected(Negation), "n't flip");
        c.Check(Verdict("Room 12", "Room 21") == Rejected(Number), "digit change");
        c.Check(Verdict("seven", "eleven") == Rejected(Number), "number word change");
        c.Check(Verdict("there", "their") == Rejected(ContextualWord), "homophone");
        c.Check(Verdict("report", "reports") == Rejected(Inflection), "plural");
        c.Check(Verdict("walk", "walked") == Rejected(Inflection), "tense");
        c.Check(Verdict("sing", "sang") == Rejected(Inflection), "vowel-only short word");
        c.Check(Verdict("Jon", "John") == Rejected(TooShort), "short names skipped");
        c.Check(Verdict("table", "chair") == Rejected(NotRespelling), "unrelated words");
        c.Check(Verdict("Acme,", "Akme,") == Rejected(UnsupportedCharacters), "punctuation inside candidate");
        c.Check(Verdict("Catherine", "Kathryn") == Accepted("Catherine", "Kathryn"), "phonetic respelling");
        c.Check(Verdict("cooper netties", "Kubernetes") == Accepted("cooper netties", "Kubernetes"), "multiword to term");
        c.Check(Verdict("the Acme team", "the Akme team") == Accepted("Acme", "Akme"), "shared context words trimmed");
        c.Check(Verdict("Fred", "Quorbix") == Rejected(NotRespelling), "dissimilar without manual entry");
        c.Check(Verdict("Fred", "Quorbix", "quorbix") == Accepted("Fred", "Quorbix"), "known manual term waives similarity");
        c.Check(Verdict("accept", "decline", "decline") == Rejected(Antonym), "manual term does not waive meaning guards");

        // Optional lexicon guard: real-word swaps are rejected, term respellings still pass.
        var lexicon = new[] { "casual", "causal", "lunch", "launch", "cloud", "acme", "team" };
        var common = Lexicon(lexicon);
        c.Check(CorrectionFilter.Evaluate("casual", "causal") == Accepted("casual", "causal"), "without lexicon a real-word swap passes (known limit)");
        c.Check(CorrectionFilter.Evaluate("casual", "causal", null, common) == Rejected(OrdinaryWords), "lexicon rejects real-word swap");
        c.Check(CorrectionFilter.Evaluate("lunch", "launch", null, common) == Rejected(OrdinaryWords), "lunch -> launch");
        c.Check(CorrectionFilter.Evaluate("Acme", "Akme", null, common) == Accepted("Acme", "Akme"), "lexicon keeps Acme -> Akme");
        c.Check(CorrectionFilter.Evaluate("Cloud", "Claude", new HashSet<string> { "claude" }, Lexicon([.. lexicon, "claude"])) == Rejected(OrdinaryWords), "manual term does not waive lexicon guard");
        // Common word -> manual rare term is contextual, not learned; unfamiliar respelling still is.
        var cd = Lexicon("cloud", "zero", "cipher");
        c.Check(CorrectionFilter.Evaluate("Cloud", "clouddev", new HashSet<string> { "clouddev" }, cd) == Rejected(OrdinaryWords), "common word is not learned toward a manual term");
        c.Check(CorrectionFilter.Evaluate("Zero cloud", "Zero clouddev", new HashSet<string> { "clouddev" }, cd) == Rejected(OrdinaryWords), "context does not make a common word learnable");
        c.Check(CorrectionFilter.Evaluate("Akmee", "Akme", new HashSet<string> { "akme" }, cd) == Accepted("Akmee", "Akme"), "unfamiliar misspelling still learned");
        c.Check(CorrectionFilter.Evaluate("cloud zero", "clouddev zero", new HashSet<string> { "clouddev zero" }, cd) == Rejected(OrdinaryWords), "two-word ordinary source extended is not learned");
        foreach (var (src, dst) in new[] { ("Cloud", "clouddev"), ("Acme", "Akme") })
        {
            var lex = Lexicon("cloud", "acme", "cloud's");
            bool learned = CorrectionFilter.Evaluate(src, dst, null, lex) is CandidateVerdict.Accepted;
            bool skipped = CorrectionFilter.EmbedsOrdinary(TermText.Fold(src), TermText.Fold(dst));
            c.Check(learned != skipped, $"learning and apply agree for {src} -> {dst}");
        }
        {
            var d = new DictionaryStore(TempDirs.New("dict-safe"));
            d.Add("clouddev", ["Cloud", "cloudnip"], EntrySource.Learned);
            d.Add("Akme", ["Acme"], EntrySource.Learned);
            d.Add("Zed", ["zero"], EntrySource.Manual);
            d.Add("GitHub", ["git hub"], EntrySource.Learned);
            var t = d.Apply("Cloud zero cloudnip Acme git hub", cd);
            c.Check(t == "Cloud Zed clouddev Akme GitHub", "old learned common alias skipped; manual, rare and spacing aliases apply: " + t);
            c.Check(d.Apply("Cloud") == "Clouddev", "without a lexicon aliases are unchanged");
            var learnedEntry = Learned(d, "clouddev");
            c.Check(learnedEntry.Variants.Where(v => !DictionaryStore.SkipLearnedVariant(v, learnedEntry, cd)).SequenceEqual(["cloudnip"]), "model vocabulary omits unsafe learned aliases");
            var manualEntry = Learned(d, "Zed");
            c.Check(!DictionaryStore.SkipLearnedVariant("zero", manualEntry, cd), "model vocabulary preserves manual aliases");
            var acronym = d.Add("CLOUDDEV", ["cloud"], EntrySource.Learned, enabled: false);
            c.Check(!DictionaryStore.SkipLearnedVariant("cloud", acronym, cd), "uppercase acronym remains eligible");
            c.Check(CorrectionFilter.Evaluate("cloud", "CLOUDDEV", null, cd) != Rejected(OrdinaryWords), "acronym learning agrees with application");
        }

        // With a lexicon: case, spacing, acronym and short-name fixes.
        string[] words = [.. lexicon, "notion", "git", "hub", "every", "one", "everyone", "api", "son", "call"];
        var ordinary = Lexicon(words);
        foreach (var (source, target) in new[] { ("jira", "Jira"), ("git hub", "GitHub"), ("para keet", "Parakeet"), ("a p i", "API"), ("J son", "JSON"), ("Jon", "John") })
            c.Check(CorrectionFilter.Evaluate(source, target, null, ordinary) == Accepted(source, target), $"{source} -> {target} learned with a lexicon");
        c.Check(CorrectionFilter.Evaluate("call jira", "call Jira", null, ordinary) == Accepted("jira", "Jira"), "case fix trims its context");
        c.Check(CorrectionFilter.Evaluate("notion", "Notion", null, ordinary) == Rejected(OrdinaryWords), "recasing an ordinary word is not learned");
        var phraseLexicon = Lexicon([.. words, "spacey", "boy"]);
        c.Check(CorrectionFilter.Evaluate("spacey boy", "spaceyboi", null, phraseLexicon) == Accepted("spacey boy", "spaceyboi"), "joined name learned despite contextual component");
        c.Check(CorrectionFilter.Evaluate("rust C", "rustc", null, Lexicon("rust", "c")) == Accepted("rust C", "rustc"), "capitalized single-letter term component is learned");
        c.Check(CorrectionFilter.Evaluate("every one", "everyone", null, ordinary) != Accepted("every one", "everyone"), "joining ordinary words is not learned");
        c.Check(CorrectionFilter.Evaluate("a boy", "aboy", null, Lexicon("a", "boy", "aboy")) != Accepted("a boy", "aboy"), "short function word join is not learned");
        var functionWordLexicon = Lexicon("a", "boy", "i", "am");
        c.Check(CorrectionFilter.Evaluate("A boy", "aboy", null, functionWordLexicon) == Rejected(ContextualWord), "sentence-case A does not enable learning");
        c.Check(CorrectionFilter.Evaluate("I am", "iam", null, functionWordLexicon) == Rejected(ContextualWord), "pronoun I does not enable learning");
        c.Check(CorrectionFilter.Evaluate("jira", "Jira") == Rejected(CaseOnly), "no lexicon: case fix skipped");
        c.Check(CorrectionFilter.Evaluate("git hub", "GitHub") == Rejected(PunctuationOnly), "no lexicon: spacing fix skipped");
        c.Check(CorrectionFilter.Evaluate("Jon", "John") == Rejected(TooShort), "no lexicon: short word skipped");
        // History, October 2026: garbled names whose consonants shift (similarity 0.57 and 0.71).
        var names = Lexicon("codecs", "codex", "claud", "claude", "buzzard", "lunch", "launch");
        c.Check(CorrectionFilter.Evaluate("ChadJuT", "ChatGPT", null, names) == Accepted("ChadJuT", "ChatGPT"), "garbled product name learned with a lexicon");
        c.Check(CorrectionFilter.Evaluate("In Buzari's", "In Buzzati's", null, names) == Accepted("Buzari", "Buzzati"), "possessive name learned as its stem");
        c.Check(CorrectionFilter.Evaluate("ChadJuT", "ChatGPT") == Rejected(NotRespelling), "no lexicon: loose respelling skipped");
        c.Check(CorrectionFilter.Evaluate("Buzari", "buzzard", null, names) == Rejected(NotRespelling), "loose respelling never lands on an ordinary word");
        c.Check(CorrectionFilter.Evaluate("codecs", "Codex", null, names) == Rejected(OrdinaryWords), "codecs -> Codex stays unlearned");
        c.Check(CorrectionFilter.Evaluate("claud", "Claude", null, names) == Rejected(OrdinaryWords), "claud -> Claude stays unlearned");
        c.Check(CorrectionFilter.Evaluate("Quorbix", "Fredlin", null, names) == Rejected(NotRespelling), "dissimilar unfamiliar words skipped");
        c.Done();
    }
}

public class StoreJsonBomTests
{
    [Fact]
    public void DecodesAFileWithAByteOrderMark()
    {
        var dir = Directory.CreateTempSubdirectory("murmur-dict-bom-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "dictionary.json"), [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("{\"version\":1,\"entries\":[]}")]);
            var store = new Murmur.Core.DictionaryStore(dir);
            Assert.Empty(store.Entries);
        }
        finally { Directory.Delete(dir, true); }
    }
}
