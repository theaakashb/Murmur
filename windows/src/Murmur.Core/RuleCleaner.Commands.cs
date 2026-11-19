// Port of app/RuleCleaner.swift (markCommands, atPhrases), split out because Dictionary uses them too.
namespace Murmur.Core;

public sealed partial class RuleCleaner
{
    private sealed record Command(string[] Words, string Mark);
    private sealed record CommandData(Command[] MarkCommands, string[][] AtPhrases);
    private static readonly CommandData Commands = SharedData.Load<CommandData>("rule_cleaner_commands");

    /// <summary>Spoken punctuation commands in Swift order: the words and the mark they become.</summary>
    public static readonly IReadOnlyList<(IReadOnlyList<string> Words, string Mark)> MarkCommands =
        Commands.MarkCommands.Select(c => ((IReadOnlyList<string>)c.Words, c.Mark)).ToArray();

    /// <summary>Spoken forms of "@", before a handle; <c>Dictionary</c> targets them too.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> AtPhrases = Commands.AtPhrases.Select(p => (IReadOnlyList<string>)p).ToArray();
}
