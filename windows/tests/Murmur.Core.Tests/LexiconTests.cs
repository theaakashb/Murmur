using System.Runtime.CompilerServices;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class LexiconTests
{
    static string Here([CallerFilePath] string p = "") => p;

    [Theory]
    [InlineData("lunch"), InlineData("launch"), InlineData("Meeting"), InlineData("report"), InlineData("schedule"),
     InlineData("don't"), InlineData("don’t"), InlineData("dog's"), InlineData("twenty-six"), InlineData("well-known"), InlineData("receive")]
    public void OrdinaryWordsAreCommon(string word) => Assert.True(Lexicon.English.IsCommonWord(word));

    [Theory]
    [InlineData("Zorvix"), InlineData("Anthropic"), InlineData("Jessica"), InlineData("GitHub"),
     InlineData("OpenAI"), InlineData("recieve"), InlineData("lunhc"), InlineData("xq-zv"), InlineData("lunch-zzzq"), InlineData("-"), InlineData(""), InlineData("  ")]
    public void NamesBrandsAndMisspellingsAreNotCommon(string word) => Assert.False(Lexicon.English.IsCommonWord(word));

    [Fact]
    public void LoadsOnceAndIsSafeFromManyThreads()
    {
        var results = Enumerable.Range(0, 64).AsParallel().Select(_ => Lexicon.English.IsCommonWord("lunch")).ToArray();
        Assert.All(results, Assert.True);
        Assert.InRange(Lexicon.English.Count, 80_000, 100_000);
    }

    // Fixture: words only, each with NSSpellChecker's verdict measured on macOS (windows/tools/lexicon).
    [Fact]
    public void AgreesWithNSSpellCheckerOnTheSample()
    {
        var rows = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(Here())!, "LexiconNSSpellChecker.tsv"))
            .Select(l => l.Split('\t')).Select(p => (word: p[0], mac: p[1] == "1")).ToList();
        Assert.Equal(4756, rows.Count);
        var agree = rows.Count(r => Lexicon.English.IsCommonWord(r.word) == r.mac);
        var falseCommon = rows.Count(r => Lexicon.English.IsCommonWord(r.word) && !r.mac);
        // Measured 4065 / 4756 (85.47%), 15 false "common". The sample leans to rare words; names and brands sit in
        // the non-list part, where the measured false "common" count is 0.
        Assert.True(agree >= 4065, $"agreement fell to {agree}/{rows.Count}");
        Assert.True(falseCommon <= 15, $"{falseCommon} words are common here but not for NSSpellChecker");
    }
}
