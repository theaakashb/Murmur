// Port of app/CleanupTypes.swift.
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Murmur.Core;

// The cleanup value vocabulary: options, tone, screen context, failure reasons, paths, guard results and outcomes.
// Shared by History, meetings and writing modes.

/// <summary>JSON string enums whose raw values are the Swift case names (lowerCamelCase).</summary>
public sealed class CamelCaseEnumConverter<T> : JsonStringEnumConverter<T> where T : struct, Enum
{
    public CamelCaseEnumConverter() : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false) { }
}

/// <summary>Same, for <see cref="Tone"/>: "very-casual".</summary>
public sealed class KebabCaseEnumConverter<T> : JsonStringEnumConverter<T> where T : struct, Enum
{
    public KebabCaseEnumConverter() : base(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) { }
}

/// <summary>Missing keys decode to the defaults, so options saved before punctuation or spoken commands existed load with them off.</summary>
public sealed record CleanupOptions
{
    [JsonConstructor]
    public CleanupOptions(bool fillers = true, bool corrections = true, bool lists = true, bool punctuation = false, bool polish = false,
                          bool professional = false, bool spoken = false, bool trustModel = false)
    {
        Fillers = fillers; Corrections = corrections; Lists = lists; Punctuation = punctuation;
        Polish = polish || professional; Professional = professional; Spoken = spoken; TrustModel = trustModel;
    }

    [JsonPropertyName("fillers")] public bool Fillers { get; init; }
    [JsonPropertyName("corrections")] public bool Corrections { get; init; }
    [JsonPropertyName("lists")] public bool Lists { get; init; }
    /// <summary>Sentence punctuation and capitalization; words are never changed.</summary>
    [JsonPropertyName("punctuation")] public bool Punctuation { get; init; }
    /// <summary>Polish instead of strict: fillers also go without commas, false starts go without a cue, and small grammar fixes are allowed.</summary>
    [JsonPropertyName("polish")] public bool Polish { get; init; }
    /// <summary>Professional, above polish (and sets it): rewrites for grammar and a professional tone.</summary>
    [JsonPropertyName("professional")] public bool Professional { get; init; }
    /// <summary>Spoken commands and numbers: "comma", "new paragraph", "two hundred thousand" -> 200,000, and the explicit email layout.</summary>
    [JsonPropertyName("spoken")] public bool Spoken { get; init; }
    /// <summary>Opt-in for Polish and Professional: accept edits without the guard; the pipeline still rejects an empty output, an answer, or a runaway length.</summary>
    [JsonPropertyName("trustModel")] public bool TrustModel { get; init; }

    /// <summary>Approved default: every comfort on. A new instance each call (the type is immutable, but keep Swift's value semantics obvious).</summary>
    public static CleanupOptions Standard => new(punctuation: true, spoken: true);
    /// <summary>Plainly labeled faithful-transcription preset: every cleanup operation off.</summary>
    public static CleanupOptions Faithful => new(fillers: false, corrections: false, lists: false);

    [JsonIgnore] public bool AnyEnabled => Fillers || Corrections || Lists || Punctuation || Spoken;
}

/// <summary>A writing mode's opt-in tone, applied by rules after cleanup. Raw values: formal, casual, very-casual, excited.</summary>
[JsonConverter(typeof(KebabCaseEnumConverter<Tone>))]
public enum Tone { Formal, Casual, VeryCasual, Excited }

public static class ToneExtensions
{
    public static string DisplayName(this Tone tone) => tone switch
    {
        Tone.Formal => "Formal",
        Tone.Casual => "Casual",
        Tone.VeryCasual => "Very casual",
        _ => "Excited",
    };

    /// <summary>Casual drops commas and the final period; Very casual also lowercases sentence starts, keeping "I" and mixed-case words; Excited turns the final period into "!".</summary>
    public static string Apply(this Tone tone, string text)
    {
        if (tone == Tone.Formal) return text;
        var t = CleanupText.Tokenize(text);
        int last = t.FindLastIndex(x => x.Kind != TokenKind.Space);
        if (last >= 0 && t[last].Text == "." && last > 0 && t[last - 1].Kind == TokenKind.Word) // Not an ellipsis.
        {
            if (tone == Tone.Excited) t[last] = new(TokenKind.Punct, "!"); else t.RemoveAt(last);
        }
        if (tone == Tone.Excited) return CleanupText.Render(t);
        int i = 0;
        while (i < t.Count)
        {
            if (t[i].IsComma)
            {
                // "a,b" keeps a space between the words; "a, b" just loses the comma.
                if (i + 1 < t.Count && t[i + 1].Kind == TokenKind.Word && i > 0 && t[i - 1].Kind == TokenKind.Word) t[i] = new(TokenKind.Space, " ");
                else { t.RemoveAt(i); continue; }
            }
            i++;
        }
        if (tone == Tone.VeryCasual)
        {
            for (int k = 0; k < t.Count; k++)
            {
                if (t[k].Kind != TokenKind.Word || !CleanupText.IsSentenceStart(t, k)) continue;
                var w = t[k].Text; var lower = t[k].Lower;
                var chars = SwiftText.Characters(w);
                if (lower == "i" || lower.StartsWith("i'", StringComparison.Ordinal) || chars.Skip(1).Any(SwiftText.IsUppercase)) continue;
                t[k] = new(TokenKind.Word, SwiftText.Lowercased(SwiftText.Prefix(w, 1)) + SwiftText.DropFirst(w));
            }
        }
        return CleanupText.Render(t);
    }
}

/// <summary>The built-in rewrites. Each has its own prompt and output check. Raw values: aiPrompt, email, notes.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<ModeTransform>))]
public enum ModeTransform { AiPrompt, Email, Notes }

/// <summary>Words per section when long text is split for parallel cleanup (the default; a provider may ask for another).</summary>
public static class CleanupSectioning
{
    public static readonly int DefaultTarget = CleanupText.Data.SectioningDefaultTarget;
}

public static class WritingModeLimits
{
    /// <summary>A writing mode's custom instructions are capped at this many characters.</summary>
    public static readonly int Instructions = CleanupText.Data.InstructionsLimit;
}

/// <summary>Reference-only text from the screen for AI cleanup routes: the selected text, the clipboard and the focused window's text.
/// Never sent on the local-rules route, never inserted, never stored.</summary>
public sealed record ScreenContext
{
    /// <summary>Pasteboard markers for passwords and other private or generated items (nspasteboard.org).</summary>
    public static readonly FrozenSet<string> PrivateTypes = CleanupText.Data.ScreenPrivateTypes.ToFrozenSet();
    public static readonly int SelectedLimit = CleanupText.Data.SelectedLimit, ClipboardLimit = CleanupText.Data.ClipboardLimit, WindowLimit = CleanupText.Data.WindowLimit;

    public string Selected { get; init; } = "";
    public string Clipboard { get; init; } = "";
    public string Window { get; init; } = "";
    public bool IsEmpty => Selected.Length == 0 && Clipboard.Length == 0 && Window.Length == 0;

    /// <summary>Null while Secure Event Input is on or a secure field is focused. A clipboard carrying any private marker type is dropped whole.
    /// Delimiter tags are stripped and each part is capped.</summary>
    public static ScreenContext? Make(string? selected, string? clipboard, IEnumerable<string> clipboardTypes, string? window, bool secureInput, bool secureField)
    {
        if (secureInput || secureField) return null;
        var clip = clipboardTypes.Any(PrivateTypes.Contains) ? null : clipboard;
        var context = new ScreenContext { Selected = Sanitized(selected, SelectedLimit), Clipboard = Sanitized(clip, ClipboardLimit), Window = Sanitized(window, WindowLimit) };
        return context.IsEmpty ? null : context;
    }

    static readonly Regex Delimiter = new(@"<\s*/?\s*(?:transcript|context|instructions)[^>]*>?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Removes anything shaped like the prompt's delimiter tags, then caps the length (in Characters).</summary>
    public static string Sanitized(string? text, int limit)
    {
        if (text is null) return "";
        var stripped = Delimiter.Replace(text, " ");
        return SwiftText.Prefix(SwiftText.TrimWhitespaceAndNewlines(stripped), limit);
    }
}

/// <summary>Why cleanup output was rejected (and a fallback used). Never carries transcript text.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<CleanupFailureReason>))]
public enum CleanupFailureReason
{
    CleanerFailed, EmptyOutput, LengthRatio, EditDistance, UnexplainedDeletion, InsertedWords, NumberAdded, NumberDropped,
    NameAdded, NameDropped, NegationChanged, AnswerPattern, StructureAdded, QuoteEdited,
}

/// <summary>Which path supplied a dictation's final text, recorded in History. Content-free.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<CleanupPath>))]
public enum CleanupPath
{
    /// <summary>No cleanup operation was enabled.</summary>
    Off,
    ModelAccepted,
    /// <summary>The model's words were rejected; its punctuation was applied to the local cleanup.</summary>
    ModelPunctuationOnLocal,
    /// <summary>The model was rejected or failed (or not configured); local rules were used.</summary>
    LocalRules,
    /// <summary>The local route: local rules, no model.</summary>
    LocalRoute,
}

/// <summary>Swift <c>enum { pass, fail(reason) }</c>.</summary>
public readonly record struct CleanupGuardResult
{
    /// <summary>Null when the guard passed.</summary>
    public CleanupFailureReason? Reason { get; init; }
    public bool IsPass => Reason is null;
    public static CleanupGuardResult Pass => default;
    public static CleanupGuardResult Fail(CleanupFailureReason reason) => new() { Reason = reason };
}

public abstract record CleanupOutcome
{
    CleanupOutcome() { }

    /// <summary>Cleanup ran and passed the guard; insert this text.</summary>
    public sealed record Cleaned(string Text) : CleanupOutcome;
    /// <summary>All options are off (or the transcript is blank); insert the raw text.</summary>
    public sealed record Unchanged(string Text) : CleanupOutcome;
    /// <summary>Save raw text, show recovery, insert only after an explicit raw-text action.</summary>
    public sealed record HeldForRecovery(string Raw, CleanupFailureReason Reason) : CleanupOutcome;
}
