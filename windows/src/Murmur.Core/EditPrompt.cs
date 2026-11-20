// Port of app/EditPrompt.swift.
namespace Murmur.Core;

public static class EditPrompt
{
    public const int AppleSelectionLimit = 3_000;
    public const int RemoteSelectionLimit = 8_000;
    public const int InstructionLimit = 500;

    sealed record Templates(string System, string User);
    static readonly Templates Text = SharedData.Load<Templates>("edit_prompt");

    public static CleanupMessages Build(string selection, string instruction, string? nonce = null)
    {
        nonce ??= Guid.NewGuid().ToString("N")[..8];
        return new(PromptTemplate.Fill(Text.System, ("nonce", nonce)),
            PromptTemplate.Fill(Text.User, ("nonce", nonce), ("selection", selection), ("instruction", instruction)));
    }
}

public static class EditCheck
{
    public abstract record EditVerdict
    {
        public sealed record Apply(string Text) : EditVerdict;
        public sealed record Unchanged : EditVerdict;
        public sealed record Refused(string Reason) : EditVerdict;
    }

    public static EditVerdict Verdict(string selection, string instruction, string output)
    {
        var edited = SwiftText.TrimWhitespaceAndNewlines(output);
        if (edited.Length == 0) return new EditVerdict.Refused("The model returned nothing.");
        if (SwiftText.CharEquals(edited, SwiftText.TrimWhitespaceAndNewlines(selection))) return new EditVerdict.Unchanged();
        if (SwiftText.Count(edited) > SwiftText.Count(selection) * 3L + 200)
            return new EditVerdict.Refused("The answer is much longer than the selection.");

        static string Lower(string text) => SwiftText.Lowercased(text).Replace('’', '\'');
        static string Start(string text) =>
            string.Concat(SwiftText.Characters(text).SkipWhile(c => !SwiftText.IsLetter(c) && !SwiftText.IsNumber(c)));

        var low = Lower(edited);
        var said = Lower(selection + " " + instruction);
        var start = Start(low);
        var saidStart = Start(said);
        foreach (var phrase in CleanupGuard.AnswerPhrases)
        {
            var prefix = string.Concat(SwiftText.Characters(phrase).SkipWhile(c => !SwiftText.IsLetter(c)));
            if (Strings.HasPrefix(start, prefix) && !Strings.HasPrefix(saidStart, prefix))
                return new EditVerdict.Refused("The model answered instead of editing.");
        }
        if (CleanupGuard.AssistantPhrases.Any(p => Strings.Contains(low, p) && !Strings.Contains(said, p)))
            return new EditVerdict.Refused("The model answered instead of editing.");
        return new EditVerdict.Apply(edited);
    }
}
