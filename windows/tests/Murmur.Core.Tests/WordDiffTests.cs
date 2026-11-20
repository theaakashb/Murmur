// Ports app/check_worddiff.swift.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class WordDiffTests
{
    static WordDiff.Run R(WordDiff.Kind k, string t, params int[] marks) => new(k, t, marks);
    static void Same(IReadOnlyList<WordDiff.Run> actual, params WordDiff.Run[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Kind, actual[i].Kind);
            Assert.Equal(expected[i].Text, actual[i].Text);
            Assert.Equal(expected[i].Marks, actual[i].Marks);
        }
    }

    [Fact]
    public void RemovedFillerAndKeptWords()
    {
        var r = WordDiff.Runs("um so the meeting is at 3 pm", "So the meeting is at 3 PM.");
        Assert.Contains(r, x => x.Kind == WordDiff.Kind.Removed && x.Text == "um");
        Assert.Contains(r, x => x.Kind == WordDiff.Kind.Same && x.Text == "the meeting is at 3");
        Assert.Contains(r, x => x.Kind == WordDiff.Kind.Tweaked && x.Text == "PM." && x.Marks.SequenceEqual(new[] { 0, 1, 2 }));
    }

    [Fact]
    public void ChangeCounts()
    {
        Assert.Equal(1, WordDiff.ChangeCount("um so the meeting is at 3 pm", "So the meeting is at 3 PM."));
        Assert.Equal(0, WordDiff.ChangeCount("same words", "same words"));
        Assert.Equal(0, WordDiff.ChangeCount("hello world", "Hello, world"));
        Assert.Empty(WordDiff.Runs("", ""));
    }

    [Fact]
    public void TweakedWordsMarkOnlyChangedCharacters()
    {
        Same(WordDiff.Runs("see the doc", "see the doc."), R(WordDiff.Kind.Same, "see the"), R(WordDiff.Kind.Tweaked, "doc.", 3));
        Same(WordDiff.Runs("ok knock it", "ok Knock it"), R(WordDiff.Kind.Same, "ok"), R(WordDiff.Kind.Tweaked, "Knock", 0), R(WordDiff.Kind.Same, "it"));
        Same(WordDiff.Runs("hello, there", "hello there").Take(1).ToArray(), R(WordDiff.Kind.Tweaked, "hello"));
        Same(WordDiff.Runs("on tuesday we", "on Tuesday. We"), R(WordDiff.Kind.Same, "on"), R(WordDiff.Kind.Tweaked, "Tuesday.", 0, 7), R(WordDiff.Kind.Tweaked, "We", 0));
        Same(WordDiff.Runs("um so the", "So the"), R(WordDiff.Kind.Removed, "um"), R(WordDiff.Kind.Tweaked, "So", 0), R(WordDiff.Kind.Same, "the"));
    }

    [Fact]
    public void RealWordChangesStayReplacements()
    {
        var real = WordDiff.Runs("see teh doc", "see the doc.");
        Assert.Contains(real, x => x.Kind == WordDiff.Kind.Removed && x.Text == "teh");
        Assert.Contains(real, x => x.Kind == WordDiff.Kind.Added && x.Text == "the");
    }

    [Fact]
    public void RevertText()
    {
        Assert.Equal(" hi there", WordDiff.RevertText("hi there", " Hi there."));
        Assert.Equal("hi there", WordDiff.RevertText("hi there", "Hi there."));
    }
}
