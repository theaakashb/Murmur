// Ports the RuleCleaner assertions of app/check_cleanup.swift (unitChecks, spokenChecks) and app/check_processing.swift
// (local capitals), plus the .NET regex translation the port relies on.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class RuleCleanerTests
{
    static string Clean(string s, CleanupOptions o) => new RuleCleaner().Clean(s, o);

    [Fact]
    public void ActuallyDeclarationsAndQuotesStayIntact()
    {
        foreach (var ordinary in new[] { "Meet Monday. Actually, Thursday works better for Alice.", "Call Sam. Actually, Alex sent the report.",
                                         "It is ready. Actually, maybe.", "She said \"Monday. Actually, Thursday. Bring notes\"." })
            Assert.Equal(ordinary, RuleCleaner.ApplyCorrections(ordinary));
    }

    [Fact]
    public void CapitalizationAfterAbbreviations()
    {
        Assert.Equal("The U.S. at 9 a.m. today. Then Dr. Patel calls", RuleCleaner.CapitalizeSentences("the U.S. at 9 a.m. today. then Dr. Patel calls"));
        Assert.Equal("Wait... Then e.g. this. Done? Yes", RuleCleaner.CapitalizeSentences("wait... then e.g. this. done? yes"));
        // check_processing.swift.
        Assert.Equal("Hello we went. Then I'm sure I said hi? Yes", RuleCleaner.CapitalizeSentences("hello we went. then i'm sure i said hi? yes"));
        Assert.Equal("It is 3.5 percent", RuleCleaner.CapitalizeSentences("it is 3.5 percent"));
    }

    [Fact]
    public void InsteadFragments()
    {
        var alternatives = new[] { ("Meet on Monday. Thursday instead.", "Meet on Thursday."), ("Paint it red. Blue instead.", "Paint it blue."),
                                   ("Book it in March. June instead.", "Book it in June."), ("Call tomorrow. Tonight instead. Bring notes.", "Call tonight. Bring notes.") };
        foreach (var options in new[] { new CleanupOptions(polish: true), new CleanupOptions(professional: true) })
        {
            foreach (var (input, output) in alternatives) Assert.Equal(output, Clean(input, options));
            foreach (var input in new[]
            {
                "Let's make pasta. Pizza instead.", "Serve pasta and pizza instead.", "Make pasta. Pizza for lunch instead.", "Discuss Monday and Tuesday. Thursday instead.",
                "Do not meet Monday. Thursday instead.", "If we meet Monday. Thursday instead.", "Paint it red. Blue instead of green.", "Meet Monday. Thursday works instead.",
                "She said \"Meet Monday. Thursday instead.\".", "Meet Monday. Blue instead.", "Meet Monday. Monday instead.", "Order 2. 3 instead.",
                "Choose blue-green. Red instead.", "Meet mid-Monday. Thursday instead.",
            }) Assert.Equal(input, Clean(input, options));
        }
        foreach (var options in new[] { CleanupOptions.Faithful, new CleanupOptions(), new CleanupOptions(corrections: false, polish: true), new CleanupOptions(corrections: false, professional: true) })
            foreach (var (input, _) in alternatives) Assert.Equal(input, Clean(input, options));
    }

    [Fact]
    public void SpokenCommandsWriteMarksAndNumbers()
    {
        Assert.Equal("up 12%", RuleCleaner.SpokenCommands("up 12 percent"));
        Assert.Equal(",", RuleCleaner.SpokenCommands("comma"));
        Assert.Equal("\n\n", RuleCleaner.SpokenCommands("new paragraph"));
        Assert.Null(RuleCleaner.EmailLayout("Write an email to the team about the launch."));
    }

    [Fact]
    public void IcuWordBoundary()
    {
        // ICU never puts \b before a format or combining character, looks past them for the previous one, and reads astral
        // letters as one word character (observed with NSRegularExpression on the Mac).
        Assert.Equal("So i\u200B am", RuleCleaner.CapitalizeSentences("so i\u200B am"));
        Assert.Equal("Hi I'm x\u0301i", RuleCleaner.CapitalizeSentences("hi i'm x\u0301i"));
        Assert.Equal("So I 𝐀i went", RuleCleaner.CapitalizeSentences("so i 𝐀i went"));
        Assert.Equal("We did it", RuleCleaner.CapitalizeSentences("we did it"));
    }
}
