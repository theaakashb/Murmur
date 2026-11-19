// Ports the CleanupPipeline and CleanupSections assertions of app/check_cleanup.swift (unitChecks, spokenChecks, the corpus bar
// and punctuation transfers), app/check_processing.swift (sections) and app/check_local_cleanup.swift (sections that keep corrections).
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class CleanupPipelineTests
{
    internal sealed record CorpusEntry(string Id, string Input, CleanupOptions Options, string Expected, string[] Tags);

    internal static readonly IReadOnlyList<CorpusEntry> Corpus =
        JsonSerializer.Deserialize<CorpusEntry[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "cleanup-corpus.json")), SharedData.JsonOptions)!;

    sealed class EmptyCleaner : ICleaner { public string Clean(string raw, CleanupOptions options) => ""; }
    sealed class ThrowingCleaner : ICleaner { public string Clean(string raw, CleanupOptions options) => throw new InvalidOperationException(); }

    static CleanupOptions O(bool fillers = true, bool corrections = true, bool lists = true, bool punctuation = false, bool polish = false,
                            bool professional = false, bool spoken = false, bool trustModel = false) =>
        new(fillers, corrections, lists, punctuation, polish, professional, spoken, trustModel);

    static readonly CleanupOptions Standard = CleanupOptions.Standard, Faithful = CleanupOptions.Faithful;
    static CleanupOutcome Process(string raw, CleanupOptions o, ICleaner? c = null) => CleanupPipeline.Process(raw, o, c ?? new RuleCleaner());
    static CleanupOutcome Judge(string raw, string cleaned, CleanupOptions o) => CleanupPipeline.Judge(raw, cleaned, o, []);
    static CleanupOutcome Cleaned(string t) => new CleanupOutcome.Cleaned(t);
    static CleanupOutcome Held(string raw, CleanupFailureReason r) => new CleanupOutcome.HeldForRecovery(raw, r);

    [Fact]
    public void OptionsAndBasicOutcomes()
    {
        Assert.Equal(O(fillers: true, corrections: true, lists: true), new CleanupOptions());
        Assert.False(Faithful.AnyEnabled);
        const string raw = "Um, meet Tuesday, sorry, Thursday.";
        Assert.Equal(new CleanupOutcome.Unchanged(raw), Process(raw, Faithful));
        Assert.Equal(Held(raw, CleanupFailureReason.CleanerFailed), Process(raw, Standard, new ThrowingCleaner()));
        Assert.Equal(Cleaned("Meet Thursday."), Process(raw, Standard));
        Assert.Equal(new CleanupOutcome.Unchanged("   "), Process("   ", Standard, new ThrowingCleaner()));
        Assert.Equal(Cleaned(""), Process("Um.", Standard));
        Assert.Equal(Held("Hello.", CleanupFailureReason.EmptyOutput), Process("Hello.", Standard, new EmptyCleaner()));
        var old = JsonSerializer.Deserialize<CleanupOptions>("""{"fillers":true,"corrections":true,"lists":true,"spoken":true}""", SharedData.JsonOptions)!;
        Assert.True(!old.TrustModel && !Standard.TrustModel && !new CleanupOptions().TrustModel, "trust is off by default and in old saved options");
        foreach (var r in Enum.GetValues<CleanupFailureReason>())
            Assert.True(JsonSerializer.Serialize(r, SharedData.JsonOptions).Trim('"').All(char.IsAsciiLetter), $"reason {r} is a bare identifier");
    }

    [Fact]
    public void SplitCuesAndActuallyFragments()
    {
        foreach (var (split, want) in new[]
        {
            ("Let's meet on Tuesday. No wait. Wednesday at noon.", "Let's meet on Wednesday at noon."), ("Call me tomorrow. No wait. Tonight.", "Call me tonight."),
            ("Call me tomorrow. Actually, tonight.", "Call me tonight."), ("Send it to Sam. Sorry, Alex.", "Send it to Alex."),
            ("Meet at the office. I mean, at home.", "Meet at home."), ("Let's make pasta. Scratch that, let's make pizza.", "Let's make pizza."),
        }) Assert.Equal(Cleaned(want), Process(split, Standard));
        const string fragment = "Move the appointment to Monday. Actually, Thursday. Send the agenda afterward.";
        Assert.Equal(Cleaned("Move the appointment to Thursday. Send the agenda afterward."), Process(fragment, Standard));
        Assert.Equal(new CleanupOutcome.Unchanged(fragment), Process(fragment, Faithful));
        Assert.Equal(Cleaned(fragment), Process(fragment, O(fillers: false, corrections: false, lists: false, punctuation: true)));
        Assert.Equal(Cleaned("She said \"Monday. Actually, Thursday. Bring notes\"."), Process("Um, she said \"Monday. Actually, Thursday. Bring notes\".", Standard));
        foreach (var keep in new[] { "I'm late. Sorry, traffic was bad.", "We shipped it. No problems so far.", "Call me tomorrow. Actually, tonight is busy." })
            Assert.Equal(Cleaned(keep), Process(keep, Standard));
    }

    [Fact]
    public void JudgeNormalizesOutput()
    {
        Assert.Equal(Cleaned("Buy:\n1. Milk\n2. Eggs"), Judge("buy milk and eggs", "Buy:  \n1. Milk  \n2. Eggs  ", O(punctuation: true, professional: true, trustModel: true)));
        foreach (var options in new[] { Standard, O(punctuation: true, polish: true), O(punctuation: true, professional: true) })
            foreach (var space in new[] { "\u00A0", "\u2009", "\u202F" })
                Assert.Equal(Cleaned($"Review PR{space}70."), Judge("Review PR 70.", $"Review PR{space}70.", options));
        Assert.Equal(Cleaned("\n"), Judge("new line", "\n", Standard));
    }

    [Fact]
    public void PunctuationTransfer()
    {
        Assert.Equal("One two three.\n\nFour five six.", CleanupPipeline.Punctuated("one two three four five six", "One two three.\n\nFour five six."));
        Assert.Equal("One two three four.", CleanupPipeline.Punctuated("one two three four", "One two\nthree four."));
        Assert.Equal("Wow! This is very new.", CleanupPipeline.Punctuated("wow! this is very new", "Wow!\n\nThis is new."));
        Assert.Equal("That went great!\n\nNow the next part is hard.", CleanupPipeline.Punctuated("that went great! now the next part is hard", "That went great!\n\nNow the next part is hard."));
        Assert.Equal("Every like give or take twenty seconds. Okay?", CleanupPipeline.Punctuated("every like give or take twenty seconds okay", "Every give or take twenty seconds. Okay?"));
        Assert.Equal("Last model I mean last agent left.", CleanupPipeline.Punctuated("Last model I mean last agent left", "Last agent left."));
        Assert.Equal("The government simply exaggerated the threat.", CleanupPipeline.Punctuated("the government simply. Exaggerated the threat", "The government simply exaggerated the threat."));
        Assert.Null(CleanupPipeline.Punctuated("she said \"hi\" to me", "She said hi to me."));
        Assert.Equal("Hey Aakash, listen I want it.", CleanupPipeline.Punctuated("hey akash listen i want it", "Hey Aakash, I want it.", ["aakash"]));
        Assert.Equal("Aakash listen.", CleanupPipeline.Punctuated("akash listen", "Aakash listen.", ["aakash"]));
    }

    [Fact]
    public void PunctuationTransfersKeepTheWordsAndPassTheGuard()
    {
        // Gating in check_cleanup.swift: always exactly the transcript's words, and always passing a punctuation-only guard.
        var punctuationOnly = O(fillers: false, corrections: false, lists: false, punctuation: true);
        var adversarial = CleanupGuardTests.AdversarialCases().Concat(CleanupGuardTests.SpokenAdversarial());
        var broken = new List<string>();
        int transferred = 0;
        foreach (var (raw, @out) in Corpus.Select(e => (e.Input, e.Expected)).Concat(adversarial.Select(a => (a.Raw, a.Output))))
        {
            if (CleanupPipeline.Punctuated(raw, @out) is not { } t) continue;
            transferred++;
            bool same = CleanupGuard.Words(t).Select(w => w.Lower).SequenceEqual(CleanupGuard.Words(raw).Select(w => w.Lower));
            if (!same || !CleanupGuard.Check(raw, t, punctuationOnly).IsPass) broken.Add($"{raw} -> {t}");
        }
        Assert.True(transferred > 0);
        Assert.Empty(broken);
    }

    sealed record Protected(Dictionary<string, int> Names, Dictionary<string, int> Numbers, Dictionary<string, int> Negations)
    {
        static bool Same(Dictionary<string, int> a, Dictionary<string, int> b) => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
        public bool NamesEqual(Protected o) => Same(Names, o.Names);
        public bool NumbersEqual(Protected o) => Same(Numbers, o.Numbers);
        public bool NegationsEqual(Protected o) => Same(Negations, o.Negations);
    }

    static Protected Protect(string s)
    {
        var p = new Protected([], [], []);
        foreach (var w in CleanupGuard.Words(s))
        {
            if (w.IsName) p.Names[w.Lower] = p.Names.GetValueOrDefault(w.Lower) + 1;
            if (CleanupText.NumberValue(w.Lower) is { } v) p.Numbers[v] = p.Numbers.GetValueOrDefault(v) + 1;
            if (CleanupText.IsNegation(w.Lower)) p.Negations[w.Lower] = p.Negations.GetValueOrDefault(w.Lower) + 1;
        }
        return p;
    }

    [Fact]
    public void CorpusUnintendedProtectedEditsStayUnderOnePercent()
    {
        // check_cleanup.swift's bar: names, numbers or negations the cleaner changed away from both the raw text and the label.
        int any = 0;
        foreach (var e in Corpus)
        {
            var @out = new RuleCleaner().Clean(e.Input, e.Options);
            Protected pe = Protect(e.Expected), po = Protect(@out), pr = Protect(e.Input);
            if ((!pe.NamesEqual(po) && !pr.NamesEqual(po)) || (!pe.NumbersEqual(po) && !pr.NumbersEqual(po)) || (!pe.NegationsEqual(po) && !pr.NegationsEqual(po))) any++;
        }
        Assert.NotEmpty(Corpus);
        Assert.True(any <= Corpus.Count / 100.0, $"{any}/{Corpus.Count} utterances with unintended protected edits");
    }

    [Fact]
    public void TrustedJudge()
    {
        var trusted = Standard with { Polish = true, TrustModel = true };
        var guarded = trusted with { TrustModel = false };
        var edits = new[] { ("Me and John was late.", "John and I were late."), ("Send the deck to John and Maria by Friday.", "Send the deck to Maria by Friday."),
                            ("We need three hundred chairs for the event.", "We need 250 chairs for the event.") };
        foreach (var (raw, @out) in edits)
        {
            Assert.Equal(Cleaned(@out), Judge(raw, @out, trusted));
            if (raw != edits[0].Item1) Assert.NotEqual(Cleaned(@out), Judge(raw, @out, guarded));
        }
        foreach (var options in new[] { O(trustModel: true), O(fillers: false, corrections: false, lists: false, trustModel: true) })
            Assert.NotEqual(Cleaned("Send the deck to Maria by Friday."), Judge("Send the deck to John and Maria by Friday.", "Send the deck to Maria by Friday.", options));
        Assert.Equal(Cleaned("We need 250 chairs for the event."), Judge("We need three hundred chairs for the event.", "We need 250 chairs for the event.", O(professional: true, trustModel: true)));
        const string ask = "Can you summarize this email for me?";
        foreach (var (raw, @out, reason) in new[]
        {
            ("Ship the release today.", "  \n", CleanupFailureReason.EmptyOutput),
            (ask, "Sure! Please paste the email and I'll summarize it.", CleanupFailureReason.AnswerPattern),
            ("Ship the release today.", string.Concat(Enumerable.Repeat("Ship the release today. ", 3)), CleanupFailureReason.LengthRatio),
        }) Assert.Equal(Held(raw, reason), Judge(raw, @out, trusted));

        var pro = O(polish: true, professional: true, trustModel: true);
        void Accepts(string raw, string @out) => Assert.Equal(Cleaned(@out), Judge(raw, @out, pro));
        void Holds(string raw, string @out) => Assert.Equal(Held(raw, CleanupFailureReason.UnexplainedDeletion), Judge(raw, @out, pro));
        const string travel = "I am willing to go pretty much anywhere to pick up the perfect jacket.";
        const string crossed = "The standup is at 7 and 8 on Tuesday for the whole team. Actually, I mean the standup is at 9 on Wednesday for the whole team.";
        Accepts(crossed, "The standup is at 9 on Wednesday for the whole team.");
        Accepts("Can you send me the quarterly report today. Could you please send me the quarterly report by the end of the day today.", "Could you please send me the quarterly report by the end of the day today?");
        Holds("The standup moved to Wednesday for the whole team this week. Actually, I mean to book the large room for it.", "I mean to book the large room.");
        Holds("We should not ship the release on Friday this week. We should ship the release on Friday this week after lunch.", "Ship it.");
        Holds("The warehouse in Austin ships orders every weekday morning. Dana has not approved the overtime budget for December yet.", "Dana has not approved the overtime budget for December yet.");
        Holds(travel, "I am willing to go.");
        Accepts(travel, "I will travel almost anywhere to find the perfect jacket.");
        Accepts("Um, uh, I I think we should, um, ship it on Friday if QA signs off.", "I think we should ship it on Friday if QA signs off.");
        Holds("We should move the review to Thursday morning and invite the design team to the kickoff call before the vendor contract expires.", "We should move the review to Thursday morning and invite the design team to the kickoff call.");
        Holds("Do not deploy the build to production unless the staging tests pass.", "Deploy the build to production when the staging tests pass.");
        Accepts("Book the room for three people, no wait, four people on Tuesday.", "Book the room for four people on Tuesday.");
        Accepts("So, you know, I was thinking, like, we should probably, you know, sort of look at the budget, I mean the budget for the quarter, and, like, maybe cut the travel line, you know, if we can.",
                "I was thinking we should look at the quarter budget and maybe cut the travel line if we can.");
        Holds("Do not ship it unless QA signs off.", "Do not ship it.");
        foreach (var (raw, @out) in new[]
        {
            ("That sounds pretty good no I mean that doesn't sound that great.", "That doesn't sound that great."), ("What time is it? No, I mean, what day is it?", "What day is it?"),
            ("Send Alex 20 dollars no I mean do not send Alex 20 dollars.", "Do not send Alex 20 dollars."),
            ("me and Priya was thinking we could ship on Friday actually no on Monday", "Priya and I were thinking we could ship on Monday."),
            ("Hey, let's meet on Monday. No, Thursday.", "Hey, let's meet on Thursday."), ("Then rename this folder. I mean, delete this folder.", "Delete this folder."),
            ("Check whether the logo is aligned and report what you find do not move it.", "Check whether the logo is aligned and report what you find, without moving it."),
        }) Accepts(raw, @out);
        foreach (var (raw, @out) in new[]
        {
            ("We can ship Monday actually no one approved it.", "We can ship Monday. Someone approved it."),
            ("Ship on Friday actually no on Monday and do not notify Sam.", "Ship on Monday and notify Sam."),
            ("Ship on Friday actually no on Monday unless QA finds a blocker.", "Ship on Monday."),
            ("Send Alex 20 dollars to the vendor and do not tell the team about the change.", "Send Alex 20 dollars to the vendor and tell the team about the change."),
            ("Send the report to Dana, I mean Dan, and then book the room and order lunch for the whole team on Friday.", "Send the report to Dan."),
            ("Send Alex 20 dollars no I mean send Alex 30 dollars and do not tell the team about it.", "Send Alex 30 dollars and tell the team about it."),
            ("Ship it Monday no I mean ship it Tuesday unless QA finds a blocker.", "Ship it Tuesday."),
        }) Holds(raw, @out);
    }

    // MARK: Spoken commands through the whole local pipeline (check_cleanup.swift spokenChecks)

    internal static readonly (string Raw, string Want)[] Converted =
    [
        ("hello comma how are you question mark", "Hello, how are you?"), ("Hello, comma, how are you? Question mark.", "Hello, how are you?"),
        ("we ship on friday period then we celebrate exclamation mark", "We ship on friday. Then we celebrate!"), ("the agenda colon budget and hiring", "The agenda: budget and hiring"),
        ("a well hyphen known fact", "A well-known fact"), ("we tried dash and failed", "We tried — and failed"), ("first point new line second point", "First point\nSecond point"),
        ("that was the first part new paragraph now the second part", "That was the first part\n\nNow the second part"), ("it ends here full stop", "It ends here."),
        ("comma", ","), ("new paragraph", "\n\n"), ("the budget is two hundred and fifty thousand", "The budget is 250,000"), ("one hundred or two hundred", "100 or 200"),
        ("one hundred and two hundred", "100 and 200"), ("it grew to two point five million users", "It grew to 2.5 million users"), ("about twelve hundred people", "About 1200 people"),
        ("two lakh fifty thousand rupees", "2,50,000 rupees"), ("three crore people", "3,00,00,000 people"), ("two million people", "2 million people"),
        ("three million five hundred thousand", "3,500,000"), ("two thousand and twenty four", "2024"), ("ping at the rate KV about the launch", "Ping @KV about the launch"),
        ("ping at sign KV", "Ping @KV"), ("my address is name at the rate gmail dot com", "My address is name@gmail.com"), ("visit example dot com", "Visit example.com"),
        ("post it with hashtag launch", "Post it with #launch"), ("see issue hash forty two", "See issue #42"), ("a ten percent raise", "A 10% raise"),
        ("growth was two point five percent", "Growth was 2.5%"), ("at the rate of ten percent", "At the rate of 10%"),
        ("write an email to Sarah saying the report is ready, thanks, best regards, Kartik", "Hi Sarah,\n\nThe report is ready, thanks.\n\nBest regards,\nKartik"),
        ("draft an email to john saying the deck is attached thanks kartik", "Hi John,\n\nThe deck is attached.\n\nThanks,\nKartik"),
        ("compose an email to Priya saying um the launch moved to Monday", "Hi Priya,\n\nThe launch moved to Monday."),
    ];

    static readonly string[] Kept =
    [
        "It took a period of time.", "Our notice period is thirty days.", "The trial period ends soon.", "The colon is part of the gut.",
        "We launched a new line of products.", "Start a new paragraph here.", "Add a comma there.", "The question mark is missing.",
        "Give me five minutes.", "I told you a hundred times.", "She came first and he was twenty first.", "Back in nineteen ninety nine.",
        "Call five five five one two three four.", "Meet at three thirty.", "In twenty twenty six.", "The two hundredth visitor won.",
        "Let's hash out a plan.", "What percentage of users?", "At the rate things are going we will be late.", "At the rate we're going, no.",
        "I'll dash home now.", "Add a dash of salt.", "He said \"comma\" loudly.", "Write an email to the team about the launch.",
        "Can you write an email for me?", "Is it ten or eleven?", "The polka dot dress.", "Connect the dot com bubble.",
    ];

    [Fact]
    public void SpokenCommandsThroughThePipeline()
    {
        var on = Standard;
        Assert.True(on.Spoken && !Faithful.Spoken && !new CleanupOptions().Spoken);
        Assert.True(O(fillers: false, corrections: false, lists: false, spoken: true).AnyEnabled, "spoken alone counts as enabled");
        foreach (var (raw, want) in Converted) Assert.Equal(Cleaned(want), Process(raw, on));
        foreach (var raw in Kept) Assert.Equal(Cleaned(raw), Process(raw, on));
    }

    // MARK: Sections (check_processing.swift sectionChecks, check_local_cleanup.swift, check_cleanup.swift)

    [Fact]
    public void SectionsSplitAtSentenceEnds()
    {
        const string sentence = "We should um, ship the build on Friday after the review.";
        static string Repeat(string s, int n) => string.Join(" ", Enumerable.Repeat(s, n));
        var longText = Repeat(sentence, 30);
        var sections = CleanupSections.Split(longText);
        Assert.Equal(3, sections.Count);
        Assert.Equal(longText, string.Join(" ", sections));
        Assert.All(sections, s => Assert.EndsWith(".", s));
        Assert.Single(CleanupSections.Split(Repeat(sentence, 10)));
        Assert.Single(CleanupSections.Split("First, " + longText + " Second, call Sam."));
        Assert.Equal(2, CleanupSections.Split(Repeat(sentence, 21)).Count);
        Assert.Single(CleanupSections.Split("write an email to Sarah saying " + string.Concat(Enumerable.Repeat("the plan is fine and ", 40)) + "best regards Kartik"));
    }

    [Fact]
    public void SectionsKeepCorrectionsAttached()
    {
        const string filler = "we reviewed the plan and agreed on the next steps for the team.";
        var text = string.Join(" ", Enumerable.Repeat(filler, 14)) + " We ship on Friday. Actually no, on Monday. " + string.Join(" ", Enumerable.Repeat(filler, 14));
        var sections = CleanupSections.Split(text, 150, keepingCorrectionsAttached: true);
        Assert.True(sections.Count > 1 && sections.All(s => !s.StartsWith("Actually no", StringComparison.Ordinal)), "a correction is never cut from its antecedent");
        Assert.Equal(text, string.Join(" ", sections));
        Assert.All(RuleCleaner.CuePatterns, c => Assert.True(CleanupSections.OpensWithCorrection(string.Join(" ", c.Words) + " Monday.")));
        Assert.True(CleanupSections.OpensWithCorrection("Or rather Monday.") && CleanupSections.OpensWithCorrection("Instead, Monday."));
        Assert.False(CleanupSections.OpensWithCorrection("Nobody came.") || CleanupSections.OpensWithCorrection("Sorrysome text.") || CleanupSections.OpensWithCorrection("We ship on Friday."));
    }
}
