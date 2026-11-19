// Ports app/check_boost_filter.swift: the dictionary rescoring filter in the transcription helper.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class BoostFilterTests
{
    // A tiny stand-in for the spell checker's lexicon.
    static readonly HashSet<string> Common = ["i", "mean", "it", "is", "not", "a", "big", "deal", "nothing", "else", "option", "works", "for", "me",
        "remind", "to", "call", "dad", "getting", "the", "build", "green", "open", "router", "through", "calls", "basin", "store", "rows", "in",
        "coming", "like", "that", "run", "get", "pods", "send", "report", "cube", "cuddle", "have", "seen", "neither", "she", "said", "i'm"];

    static string F(string raw, string rescored, params string[] aliases) => BoostFilter.Filter(raw, rescored, aliases.ToHashSet(), Common.Contains);

    [Fact]
    public void FilterChecks()
    {
        var c = new Checker();
        // Measured false positives (synthetic speech, 20-term dictionary): restored.
        c.Check(F("Output the word approved and nothing else.", "Output the word approved and Notion else.") == "Output the word approved and nothing else.", "nothing -> Notion reverted");
        c.Check(F("Neither option works for me.", "Neither Notion works for me.") == "Neither option works for me.", "option -> Notion reverted");
        c.Check(F("She said, um, I'm not coming.", "She said, um, I'm Notion") == "She said, um, I'm not coming.", "two common words -> one term reverted");
        c.Check(F("Remind me to call dad at 7.", "Gemini me to call dad at 7.") == "Remind me to call dad at 7.", "sentence-initial common word reverted");
        c.Check(F("I mean, it is not a big deal.", "I Sean, it is not a big deal.") == "I mean, it is not a big deal.", "mean -> Sean reverted");
        // Measured true positives: kept.
        c.Check(F("Run cubectal get pods.", "Run kubectl get pods.") == "Run kubectl get pods.", "misheard non-word kept");
        c.Check(F("Send the ShelfMud report.", "Send the Shelfmate report.") == "Send the Shelfmate report.", "ShelfMud kept");
        c.Check(F("Store the Rosin Supa Basin root calls through open router.", "Store the Rosin Supabase root calls through OpenRouter") == "Store the Rosin Supabase root calls through OpenRouter.", "partly common span and joined spelling kept");
        c.Check(F("Push the change to CloudEv now.", "Push the change to Clouddev now.") == "Push the change to Clouddev now.", "CloudEv kept");
        // A listed alias is kept even when its words are ordinary.
        c.Check(F("run cube cuddle now", "run kubectl now", "cube cuddle") == "run kubectl now", "alias of common words kept");
        c.Check(F("run cube cuddle now", "run kubectl now") == "run cube cuddle now", "unlisted common words reverted");
        // Mixed: one kept, one reverted, in the same text.
        c.Check(F("Run cubectal, I mean nothing else.", "Run kubectl, I Sean Notion else.") == "Run kubectl, I mean nothing else.", "per-span decisions");
        c.Check(F("Unchanged text.", "Unchanged text.") == "Unchanged text.", "identical text");
        c.Check(F("Ask Priya about it.", "Ask Notion it.") == "Ask Priya about it.", "a name is not swallowed with its neighbours");
        c.Check(BoostFilter.Similarity("akash", "aakash") > 0.8 && BoostFilter.Similarity("", "") == 1, "similarity");
        c.Check(F("Nor does it support offline mode.", "Nor does it support offline mode") == "Nor does it support offline mode.", "punctuation the rescorer dropped is kept");
        c.Check(F("Bring it, I mean, if you can.", "Bring it, I Sean if you can.") == "Bring it, I mean, if you can.", "punctuation around a reverted span is kept");
        c.Check(F("", "") == "", "empty");
        c.Done();
    }

    [Theory]
    [InlineData("El Ni<unk>o", "El Nio")]
    [InlineData("<unk> hi", "hi")]
    [InlineData("hi <unk>", "hi")]
    [InlineData("a <unk> b", "a b")]
    [InlineData("hi <unk>.", "hi.")]
    [InlineData("<unk><unk>", "")]
    [InlineData("<unk>", "")]
    [InlineData("No unknown here.", "No unknown here.")]
    public void UnknownPieceNeverReachesDictationText(string heard, string written) => Assert.Equal(written, BoostFilter.WithoutUnknown(heard));
}
