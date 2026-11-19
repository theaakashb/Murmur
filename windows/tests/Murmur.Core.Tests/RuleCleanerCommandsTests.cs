using Murmur.Core;

public class RuleCleanerCommandsTests
{
    [Fact]
    public void LoadsCommandsInSwiftOrder()
    {
        Assert.Equal(new[] { "new", "paragraph" }, RuleCleaner.MarkCommands[0].Words);
        Assert.Equal("\n\n", RuleCleaner.MarkCommands[0].Mark);
        Assert.Contains(RuleCleaner.MarkCommands, c => c.Words.SequenceEqual(new[] { "dash" }) && c.Mark == "—");
        Assert.Equal(new[] { "at", "the", "rate" }, RuleCleaner.AtPhrases[0]);
    }
}
