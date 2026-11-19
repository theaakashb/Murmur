using System.Reflection;

namespace Murmur.App;

internal static class AppInfo
{
    public static readonly string Version = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}

internal static class TextRules
{
    /// <summary>Insertion.swift:182-192: a leading space when the previous character is not whitespace or an opening bracket and the selection is empty.
    /// shortcut: FocusProbe gives the field's text but not the caret offset, so the caret is assumed to be at the end (no following letter is checked);
    /// replace once the Platform probe reports the caret.</summary>
    public static string WithLeadingSpace(string text, string? fieldValue, string? selection)
    {
        if (fieldValue is not { Length: > 0 } || !string.IsNullOrEmpty(selection) || text.Length == 0 || char.IsWhiteSpace(text[0])) return text;
        var previous = fieldValue[^1];
        return char.IsWhiteSpace(previous) || previous is '(' or '[' or '{' ? text : " " + text;
    }
}
