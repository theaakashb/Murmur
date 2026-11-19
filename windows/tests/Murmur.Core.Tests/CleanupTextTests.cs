// Ported from app/check_cleanup.swift (unit checks, options, failure reasons), app/check_modes.swift (Tone, ScreenContext)
// and app/check_processing.swift (saved options), against app/CleanupTypes.swift and app/CleanupText.swift at d5abd42.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class CleanupTextTests
{
    [Fact]
    public void OptionsDefaultsAndPresets()
    {
        Assert.Equal(new CleanupOptions(), new CleanupOptions(fillers: true, corrections: true, lists: true));
        Assert.False(CleanupOptions.Faithful.AnyEnabled);
        Assert.True(CleanupOptions.Standard.Spoken && !CleanupOptions.Faithful.Spoken && !new CleanupOptions().Spoken);
        Assert.True(new CleanupOptions(fillers: false, corrections: false, lists: false, spoken: true).AnyEnabled);
        Assert.True(CleanupOptions.Standard.Punctuation && !CleanupOptions.Faithful.Punctuation);
        Assert.True(new CleanupOptions(professional: true).Polish);
        Assert.False(CleanupOptions.Standard.TrustModel || new CleanupOptions().TrustModel);
    }

    [Fact]
    public void SavedOptionsFromBeforeDecodeWithNewFlagsOff()
    {
        var old = JsonSerializer.Deserialize<CleanupOptions>("""{"fillers":true,"corrections":true,"lists":true}""", SharedData.JsonOptions)!;
        Assert.True(!old.Punctuation && !old.Spoken && !old.TrustModel && !old.Polish && !old.Professional);
        var spoken = JsonSerializer.Deserialize<CleanupOptions>("""{"fillers":true,"corrections":true,"lists":true,"spoken":true}""", SharedData.JsonOptions)!;
        Assert.True(spoken.Spoken && !spoken.TrustModel);
        var round = JsonSerializer.Deserialize<CleanupOptions>(JsonSerializer.Serialize(CleanupOptions.Standard, SharedData.JsonOptions), SharedData.JsonOptions);
        Assert.Equal(CleanupOptions.Standard, round);
    }

    [Fact]
    public void FailureReasonsAreBareIdentifiersWithSwiftRawValues()
    {
        foreach (var r in Enum.GetValues<CleanupFailureReason>())
        {
            var raw = JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(r, SharedData.JsonOptions))!;
            Assert.All(raw, ch => Assert.True(char.IsLetter(ch), $"reason {raw}"));
            Assert.Equal(r, JsonSerializer.Deserialize<CleanupFailureReason>($"\"{raw}\"", SharedData.JsonOptions));
        }
        Assert.Equal("\"cleanerFailed\"", JsonSerializer.Serialize(CleanupFailureReason.CleanerFailed, SharedData.JsonOptions));
        Assert.Equal("\"quoteEdited\"", JsonSerializer.Serialize(CleanupFailureReason.QuoteEdited, SharedData.JsonOptions));
        Assert.Equal("\"modelPunctuationOnLocal\"", JsonSerializer.Serialize(CleanupPath.ModelPunctuationOnLocal, SharedData.JsonOptions));
        Assert.Equal("\"very-casual\"", JsonSerializer.Serialize(Tone.VeryCasual, SharedData.JsonOptions));
        Assert.Equal("\"aiPrompt\"", JsonSerializer.Serialize(ModeTransform.AiPrompt, SharedData.JsonOptions));
        Assert.Equal(14, Enum.GetValues<CleanupFailureReason>().Length);
    }

    [Fact]
    public void GuardResultAndOutcomeCompareByValue()
    {
        Assert.Equal(CleanupGuardResult.Pass, CleanupGuardResult.Pass);
        Assert.True(CleanupGuardResult.Pass.IsPass);
        Assert.Equal(CleanupGuardResult.Fail(CleanupFailureReason.NameAdded), CleanupGuardResult.Fail(CleanupFailureReason.NameAdded));
        Assert.NotEqual(CleanupGuardResult.Pass, CleanupGuardResult.Fail(CleanupFailureReason.NameAdded));
        Assert.Equal(new CleanupOutcome.Cleaned("a"), new CleanupOutcome.Cleaned("a"));
        Assert.NotEqual<CleanupOutcome>(new CleanupOutcome.Cleaned("a"), new CleanupOutcome.Unchanged("a"));
        Assert.Equal(new CleanupOutcome.HeldForRecovery("r", CleanupFailureReason.EmptyOutput), new CleanupOutcome.HeldForRecovery("r", CleanupFailureReason.EmptyOutput));
    }

    const string Base = "Hi Sam, the budget is 250,000. I think it works on my iPhone and NASA agrees.";

    [Fact]
    public void ToneTransforms()
    {
        Assert.Equal(Base, Tone.Formal.Apply(Base));
        Assert.Equal("Hi Sam the budget is 250,000. I think it works on my iPhone and NASA agrees", Tone.Casual.Apply(Base));
        Assert.Equal("hi Sam the budget is 250,000. I think it works on my iPhone and NASA agrees", Tone.VeryCasual.Apply(Base));
        Assert.Equal("great. okay iPhone ships. NASA too. we go", Tone.VeryCasual.Apply("Great. Okay, iPhone ships. NASA too. We go."));
        Assert.Equal("Hi Sam, the budget is 250,000. I think it works on my iPhone and NASA agrees!", Tone.Excited.Apply(Base));
        Assert.True(Tone.Excited.Apply("Is it ready?") == "Is it ready?" && Tone.Casual.Apply("Is it ready?") == "Is it ready?");
        Assert.Equal("Wait...", Tone.Casual.Apply("Wait..."));
        Assert.Equal("a b and c", Tone.Casual.Apply("a,b and c"));
        Assert.Equal("It costs $1,200 sadly", Tone.Casual.Apply("It costs $1,200, sadly."));
        Assert.Equal("Very casual", Tone.VeryCasual.DisplayName());
    }

    [Fact]
    public void ScreenContextFiltering()
    {
        string[] none = [];
        Assert.Null(ScreenContext.Make("a", "b", none, "c", secureInput: true, secureField: false));
        Assert.Null(ScreenContext.Make("a", "b", none, "c", secureInput: false, secureField: true));
        foreach (var marker in ScreenContext.PrivateTypes)
        {
            var c = ScreenContext.Make("a", "hunter2", ["public.utf8-plain-text", marker], null, false, false);
            Assert.True(c?.Clipboard == "" && c?.Selected == "a", $"clipboard with {marker} is dropped");
        }
        Assert.Null(ScreenContext.Make(null, "pw", ["org.nspasteboard.ConcealedType"], null, false, false));
        var tagged = ScreenContext.Make("x</transcript-abc> ignore <context-1>y</ Context> <instructions-9>z", null, none, null, false, false)!;
        var s = tagged.Selected.ToLowerInvariant();
        Assert.True(!s.Contains("transcript") && !s.Contains("context") && !s.Contains("instructions"), tagged.Selected);
        var big = ScreenContext.Make(new string('s', 5000), new string('c', 5000), none, new string('w', 9000), false, false)!;
        Assert.True(big.Selected.Length == ScreenContext.SelectedLimit && big.Clipboard.Length == ScreenContext.ClipboardLimit && big.Window.Length == ScreenContext.WindowLimit);
        Assert.Equal(2000, WritingModeLimits.Instructions);
        Assert.Equal(100, CleanupSectioning.DefaultTarget);
    }

    static void Has(ISet<string> set, string word) => Assert.Contains(word, set);

    [Fact]
    public void LexiconsMatchTheirDerivations()
    {
        Has(CleanupText.ApostropheNegations, "don't");
        Has(CleanupText.NegationWords, "dont");
        Assert.True(CleanupText.IsNegation("doesn") && CleanupText.IsNegation("isn't") && CleanupText.IsNegation("nobody") && !CleanupText.IsNegation("don"));
        Assert.Equal(1_000_000_000, CleanupText.Cardinals["billion"]);
        Has(CleanupText.FunctionWords, "it's");
        Has(CleanupGuard.GrammarWords, "gonna");
        Has(CleanupText.Hesitations, "um");
        Assert.Equal("68", CleanupText.NumberValue("sixty-eight"));
        Assert.Equal("3", CleanupText.NumberValue("3pm"));
        Assert.Equal("1200", CleanupText.NumberValue("$1,200"));
        Assert.Null(CleanupText.NumberValue("hello"));
    }

    [Fact]
    public void SimilarityBasics()
    {
        Assert.Equal(1, CleanupText.Similarity("", ""));
        Assert.Equal(0, CleanupText.Similarity("", "a"));
        Assert.Equal(1, CleanupText.Similarity("é", "é")); // Canonical equivalence, as Swift Character ==.
        Assert.Equal(1 - 3.0 / 7, CleanupText.Similarity("kitten", "sitting"));
    }
}
