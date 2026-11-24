// SpeechSeams: where long recordings are cut and how the decoded pieces are joined. Synthetic text and samples only.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class SpeechSeamsTests
{
    static string Join(params string?[] pieces) => SpeechSeams.Join(pieces);

    [Theory]
    [InlineData("we went to the", "the market today", "we went to the market today")]
    [InlineData("we went to the market", "the market today", "we went to the market today")]
    [InlineData("so we went to the market", "we went to the market today", "so we went to the market today")]
    [InlineData("one two three four five", "two three four five six", "one two three four five six")]
    [InlineData("alpha beta gamma delta epsilon zeta", "alpha beta gamma delta epsilon zeta eta", "alpha beta gamma delta epsilon zeta eta")]
    public void ExactDuplicatesAtASeamAreDroppedOnce(string a, string b, string expected) => Assert.Equal(expected, Join(a, b));

    [Fact]
    public void OneToSixWordOverlapsAllDrop()
    {
        var words = "red orange yellow green blue indigo violet".Split(' ');
        for (int k = 1; k <= 6; k++)
        {
            var a = "start " + string.Join(' ', words.Take(k));
            var b = string.Join(' ', words.Take(k)) + " end";
            Assert.Equal(a + " end", Join(a, b));
        }
    }

    [Fact]
    public void AnOverlapOfSevenWordsIsNotDropped()
    {
        // The window is six words: a longer run is not treated as a seam overlap.
        Assert.Equal("a b c d e f g a b c d e f g", Join("a b c d e f g", "a b c d e f g"));
    }

    [Fact]
    public void CaseAndPunctuationDoNotHideAnOverlapAndTheEarlierCopyIsKept()
    {
        Assert.Equal("He said, \"Hello there.\" Then left.", Join("He said, \"Hello there.\"", "hello THERE Then left."));
        Assert.Equal("It is done done. Next, we go.", Join("It is done done.", "done done Next, we go."));
        Assert.Equal("wait, wait... what is it", Join("wait, wait...", "wait wait what is it"));
        Assert.Equal("Dr. Smith's plan works", Join("Dr. Smith's", "smiths plan works"));
        Assert.Equal("The café is open. Come in", Join("The café is open.", "CAFÉ is open Come in"));
    }

    [Fact]
    public void AStandaloneDashOrEllipsisStaysWithItsWord()
    {
        Assert.Equal("we left — and then", Join("we left —", "left and then"));
        Assert.Equal("we left and then", Join("we left", "left — and then"));
    }

    [Fact]
    public void OneRepeatedWordAfterAFinishedSentenceIsKept()
    {
        Assert.Equal("Okay. Okay, so we start", Join("Okay.", "Okay, so we start"));
        Assert.Equal("Is it done? Done, yes", Join("Is it done?", "Done, yes"));
        Assert.Equal("He said \"stop.\" Stop now", Join("He said \"stop.\"", "Stop now"));
        // Mid-sentence, one word is the classic seam repeat and drops.
        Assert.Equal("we went to the market", Join("we went to the", "the market"));
    }

    [Fact]
    public void WordsThatDoNotMatchAreNeverDropped()
    {
        Assert.Equal("the cat sat on a mat", Join("the cat sat", "on a mat"));
        Assert.Equal("we went to the market the park is nice", Join("we went to the market", "the park is nice"));
        Assert.Equal("one two three", Join("one two", "three"));
    }

    [Fact]
    public void LegitimateRepeatsInsideAPieceAreKept()
    {
        Assert.Equal("I know that that is true. Then we left", Join("I know that that is true.", "Then we left"));
        Assert.Equal("I said that that day was long and it was", Join("I said that that day was long", "and it was"));
        Assert.Equal("no no no way. It is fine", Join("no no no way.", "It is fine"));
        // A run of repeats inside the next piece, not matching the previous piece's end, stays whole.
        Assert.Equal("and so on and on and on", Join("and so on", "and on and on"));
    }

    [Fact]
    public void EmptyAndWhitespacePiecesAreSkipped()
    {
        Assert.Equal("", Join());
        Assert.Equal("", Join("", "  ", null));
        Assert.Equal("hello world", Join("", "hello world", "   ", null));
        Assert.Equal("hello world there", Join("hello world", "", "world there"));
        Assert.Equal("hello world", Join("hello world", "   "));
    }

    [Fact]
    public void ALongPieceFullyInsideThePreviousEndLeavesNothing()
    {
        Assert.Equal("see you soon", Join("see you soon", "you soon"));
        Assert.Equal("see you soon", Join("see you soon", "See you soon!"));
    }

    [Fact]
    public void LeadingAndTrailingWhitespaceIsTrimmed()
    {
        Assert.Equal("we went to the market today", Join("  we went to the \n", "\t the market today  "));
        Assert.Equal("a b c", Join(" a ", " b ", " c "));
        Assert.Equal("one two", Join("one  ", " two"));
    }

    [Fact]
    public void ThreePiecesJoinTwoSeams()
    {
        Assert.Equal("first part of the story continues into the end", Join("first part of the", "part of the story continues into", "into the end"));
    }

    [Fact]
    public void ChineseAndJapaneseCompareCharactersAndJoinWithoutASpace()
    {
        Assert.Equal("我们今天去了市场买东西", Join("我们今天去了市场", "市场买东西"));
        Assert.Equal("我们今天去了市场，买东西。", Join("我们今天去了市场，", "市场，买东西。"));
        Assert.Equal("今日は天気がいいです", Join("今日は天気が", "天気がいいです"));
        Assert.Equal("วันนี้อากาศดี", Join("วันนี้อากา", "อากาศดี"));
    }

    [Fact]
    public void NoSpaceScriptsNeedTwoCharactersAndNeverDropWhatDoesNotMatch()
    {
        Assert.Equal("你好吗吗", Join("你好吗", "吗")); // one character is a legitimate repeat
        Assert.Equal("我们去了市场然后回家", Join("我们去了市场", "然后回家"));
        Assert.Equal("谢谢你", Join("谢谢你", "谢谢你")); // a whole-piece match is a seam overlap
    }

    [Fact]
    public void ACharacterOverlapIsDroppedWholeAndNothingElse()
    {
        Assert.Equal("很好", Join("很好", "很好"));
        Assert.Equal("我很好吗", Join("我很好", "很好吗"));
        Assert.Equal("我们去了市场，然后回家", Join("我们去了市场，", "去了市场，然后回家"));
    }

    [Fact]
    public void MixedTextUsesWordsWhereThereAreSpaces()
    {
        Assert.Equal("open the 文件 menu", Join("open the 文件", "文件 menu"));
        Assert.Equal("use Python today", Join("use Python", "python today"));
        Assert.Equal("你好 world hello", Join("你好 world", "hello"));
    }

    // ---- Cut selection ----

    const int Rate = 16000;

    static float[] Speechy(int length, params (int From, int To)[] silences)
    {
        var samples = new float[length];
        for (int i = 0; i < length; i++) samples[i] = 0.3f * (i % 2 == 0 ? 1 : -1);
        foreach (var (from, to) in silences) Array.Clear(samples, from, to - from);
        return samples;
    }

    static int Cut(float[] samples, Silence[] pauses, int start = 0) =>
        SpeechSeams.ChooseCut(samples, pauses, start, 15 * Rate, 28 * Rate, Rate / 5, 6 * Rate, Rate / 20);

    [Fact]
    public void TheLongestPauseInTheWindowWinsNotTheLatestOne()
    {
        var pauses = new[] { new Silence(16 * Rate, 16 * Rate + 8000), new Silence(20 * Rate, 21 * Rate), new Silence(27 * Rate, 27 * Rate + 9600) };
        Assert.Equal(20 * Rate + Rate / 2, Cut(new float[45 * Rate], pauses));
    }

    [Fact]
    public void ATieGoesToTheLaterPause()
    {
        var pauses = new[] { new Silence(17 * Rate, 18 * Rate), new Silence(24 * Rate, 25 * Rate) };
        Assert.Equal(24 * Rate + Rate / 2, Cut(new float[45 * Rate], pauses));
    }

    [Fact]
    public void PausesOutsideTheWindowAreIgnoredAndAnOverlappingPauseIsClipped()
    {
        var early = new Silence(5 * Rate, 9 * Rate);               // before start + 15 s
        var late = new Silence(29 * Rate, 33 * Rate);              // after start + 28 s
        var straddling = new Silence(27 * Rate, 30 * Rate);        // clipped to 27..28 s
        Assert.Equal(27 * Rate + Rate / 2, Cut(new float[45 * Rate], [early, late, straddling]));
        Assert.Equal(20 * Rate + 4000, Cut(new float[45 * Rate], [early, late, new Silence(20 * Rate, 20 * Rate + 8000)]));
    }

    [Fact]
    public void TheWindowFollowsThePieceStart()
    {
        var pauses = new[] { new Silence(20 * Rate, 21 * Rate), new Silence(40 * Rate, 41 * Rate) };
        Assert.Equal(40 * Rate + Rate / 2, Cut(new float[70 * Rate], pauses, start: 20 * Rate + Rate / 2));
    }

    [Fact]
    public void WithoutAPauseTheQuietestFrameNearTheEndOfTheWindowIsUsed()
    {
        var samples = Speechy(45 * Rate, (25 * Rate, 25 * Rate + 800)); // a 50 ms dip, shorter than the minimum pause
        int cut = Cut(samples, []);
        Assert.InRange(cut, 25 * Rate, 25 * Rate + 800);
        // Flat audio: still a cut inside the window.
        Assert.InRange(Cut(Speechy(45 * Rate), []), 22 * Rate, 28 * Rate);
    }

    [Fact]
    public void ShortGapsBelowTheMinimumPauseAreNotPauses()
    {
        var samples = Speechy(45 * Rate, (24 * Rate, 24 * Rate + 800));
        int cut = Cut(samples, [new Silence(24 * Rate, 24 * Rate + 800)]);
        Assert.InRange(cut, 24 * Rate, 24 * Rate + 800);
    }

    [Fact]
    public void ACutNeverPassesTheEndOfTheRecording()
    {
        Assert.True(Cut(new float[26 * Rate], []) <= 26 * Rate);
    }
}
