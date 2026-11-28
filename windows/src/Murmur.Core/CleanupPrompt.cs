// Port of app/CleanupPrompt.swift.
using System.Text.RegularExpressions;

namespace Murmur.Core;

// Prompts sent to cleanup models: the cleanup messages and the mode-transform contract.
// The texts are templates exported from the Swift prompts (shared/cleanup_prompt.json); this file holds the assembly logic.

/// <summary>The typed request behind a built prompt, for the managed local model, which builds its own prompt.</summary>
public sealed record CleanupInput
{
    /// <summary>The transcript after Professional's comma-free corrections, before spoken commands: what the local model takes.</summary>
    public required string Raw { get; init; }
    public required CleanupOptions Options { get; init; }
    /// <summary>The dictionary terms as given.</summary>
    public IReadOnlyList<string> Terms { get; init; } = [];
    /// <summary>Screen context, null when none was given or it is empty.</summary>
    public ScreenContext? Context { get; init; }

    public bool Equals(CleanupInput? o) => o is not null && Raw == o.Raw && Options == o.Options && Terms.SequenceEqual(o.Terms) && Context == o.Context;
    public override int GetHashCode() => HashCode.Combine(Raw, Options, Context);
}

public sealed record CleanupMessages(string System, string User)
{
    /// <summary>Set only by <see cref="CleanupPrompt.Build"/>; null for rewrite presets and meeting notes.</summary>
    public CleanupInput? Input { get; init; }
    /// <summary>Set only by <see cref="TransformPrompt.Build"/>: the typed preset request, for the managed local model.</summary>
    public PresetInput? Preset { get; init; }
}

public sealed record PresetInput(ModeTransform Kind, string Transcript, IReadOnlyList<string> Terms);

/// <summary>Fills the {name} placeholders of an exported prompt template in one pass, so inserted text is never re-read.</summary>
static class PromptTemplate
{
    static readonly Regex Placeholder = new(@"\{([a-z]+)\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static string Fill(string template, params (string Name, string Value)[] values) =>
        Placeholder.Replace(template, m => values.FirstOrDefault(v => v.Name == m.Groups[1].Value).Value ?? m.Value);

    /// <summary>Swift <c>nonce.filter { $0.isLetter || $0.isNumber }</c>.</summary>
    public static string Id(string nonce) => string.Concat(SwiftText.Characters(nonce).Where(c => SwiftText.IsLetter(c) || SwiftText.IsNumber(c)));

    /// <summary>Swift's default nonce, <c>UUID().uuidString</c>.</summary>
    public static string NewNonce() => Guid.NewGuid().ToString("D").ToUpperInvariant();
}

public static class CleanupPrompt
{
    internal sealed record RewriteLines(string Fillers, string Corrections, string Punctuation, string Lists, string Grammar);
    internal sealed record RewriteData(string Frame, string NoOps, string OpsHeader, RewriteLines Standard, RewriteLines Professional);
    internal sealed record StrictData(string Frame, string Spoken, string NoOps, string OpsHeader, string Fillers, string Corrections, string Punctuation, string Lists);
    internal sealed record SpellingsData(string Polish, string Strict, string Separator);
    internal sealed record ContextUserData(string Open, string Close, string Separator, string Selected, string Clipboard, string Window, string Names);
    internal sealed record PromptData(string StyleNotesTail, string ContextTail, string SpellingsTail, RewriteData Rewrite, StrictData Strict,
                                      SpellingsData Spellings, string Notes, string ContextSystem, string User, ContextUserData Context);
    internal sealed record TransformData(string PreviousAIPromptBaseline, string AiPromptDetailRule, string PreviousDetailAIPromptBaseline,
                                         string AiPromptConditionRule, string PreviousConditionalAIPromptBaseline, string PreviousEmailBaseline,
                                         string PreviousNotesBaseline, string PreviousStructuredAIPromptBaseline, string PreviousStructuredEmailBaseline,
                                         string PreviousStructuredNotesBaseline, Dictionary<string, string> Baseline, string Frame,
                                         Dictionary<string, string> Purpose, string Terms, string TermsSeparator, string Notes, string User);
    internal sealed record ExportData(PromptData Cleanup, TransformData Transform);

    internal static readonly ExportData All = SharedData.Load<ExportData>("cleanup_prompt");
    static readonly PromptData P = All.Cleanup;

    /// <summary>Where the per-call tails start in a cleanup system prompt. <c>Processing.promptHash</c> cuts at these.</summary>
    public static readonly string StyleNotesTail = P.StyleNotesTail, ContextTail = P.ContextTail, SpellingsTail = P.SpellingsTail;
    public static readonly IReadOnlyList<string> Tails = [StyleNotesTail, ContextTail, SpellingsTail];

    /// <summary>Polish and Professional: the rewrite levels.</summary>
    static string RewriteSystem(CleanupOptions options)
    {
        bool pro = options.Professional;
        var l = pro ? P.Rewrite.Professional : P.Rewrite.Standard;
        var ops = new List<string>();
        if (options.Fillers) ops.Add(l.Fillers);
        // Polish lists corrections first: without the comma-free pre-pass Professional gets, gpt-oss-20b otherwise kept both versions.
        if (options.Corrections) ops.Insert(pro ? ops.Count : 0, l.Corrections);
        if (options.Punctuation) ops.Add(l.Punctuation);
        if (options.Lists) ops.Add(l.Lists);
        if (ops.Count > 0) ops.Add(l.Grammar);
        return PromptTemplate.Fill(P.Rewrite.Frame, ("ops", ops.Count == 0 ? P.Rewrite.NoOps : string.Join("\n", ops.Prepend(P.Rewrite.OpsHeader))));
    }

    /// <summary>Options without Polish. Murmur runs Strict on the Mac and never sends it; this stays for the guard checks and the benchmark's strict mode.</summary>
    static string StrictSystem(CleanupOptions options)
    {
        var s = P.Strict;
        var ops = new List<string>();
        if (options.Fillers) ops.Add(s.Fillers);
        if (options.Corrections) ops.Add(s.Corrections);
        if (options.Punctuation) ops.Add(s.Punctuation);
        if (options.Lists) ops.Add(s.Lists);
        return PromptTemplate.Fill(s.Frame, ("ops", ops.Count == 0 ? s.NoOps : string.Join("\n", ops.Prepend(s.OpsHeader))), ("spoken", options.Spoken ? s.Spoken : ""));
    }

    /// <summary>Builds provider messages. The transcript is delimited data; only enabled operations are described.
    /// <paramref name="dictionaryTerms"/> should already be filtered to entries relevant to this transcript, since cloud providers receive them.
    /// <paramref name="context"/> (screen text) and <paramref name="instructions"/> (a writing mode's notes) are for AI routes only.</summary>
    public static CleanupMessages Build(string transcript, CleanupOptions options, IReadOnlyList<string>? dictionaryTerms = null, string? nonce = null,
                                        ScreenContext? context = null, string instructions = "")
    {
        dictionaryTerms ??= [];
        var id = PromptTemplate.Id(nonce ?? PromptTemplate.NewNonce());
        var corrected = options.Professional && options.Corrections ? CleanupGuard.ApplyCommaFreeCorrections(transcript) : transcript;
        // Spoken punctuation, numbers, symbols and the email frame are written out here by the Mac's own rules, not described to the model.
        // The guard still judges against the transcript as spoken.
        var sent = options.Spoken ? RuleCleaner.SpokenCommands(corrected) : corrected;
        var system = options.Polish ? RewriteSystem(options) : StrictSystem(options);
        var terms = new List<string>();
        foreach (var raw in dictionaryTerms)
        {
            var term = Strings.TrimWhitespaces(raw.Replace("\n", " ").Replace("\r", " "));
            if (term.Length > 0 && !terms.Any(t => SwiftText.CharEquals(t, term)) && terms.Count < 50) terms.Add(term);
        }
        if (terms.Count > 0) system += PromptTemplate.Fill(options.Polish ? P.Spellings.Polish : P.Spellings.Strict, ("terms", string.Join(P.Spellings.Separator, terms)));
        var notes = ScreenContext.Sanitized(instructions, WritingModeLimits.Instructions);
        if (notes.Length > 0) system += PromptTemplate.Fill(P.Notes, ("id", id), ("notes", notes));
        bool hasContext = context is { IsEmpty: false };
        if (hasContext) system += P.ContextSystem;
        var user = PromptTemplate.Fill(P.User, ("id", id), ("sent", sent));
        if (hasContext)
        {
            var c = P.Context;
            var parts = new[] { (c.Selected, context!.Selected), (c.Clipboard, context.Clipboard), (c.Window, context.Window) }
                .Where(x => x.Item2.Length > 0).Select(x => PromptTemplate.Fill(x.Item1, ("text", ScreenContext.Sanitized(x.Item2, ScreenContext.WindowLimit))));
            user = PromptTemplate.Fill(c.Open, ("id", id)) + string.Join(c.Separator, parts) + PromptTemplate.Fill(c.Close, ("id", id)) + user + PromptTemplate.Fill(c.Names, ("id", id));
        }
        return new CleanupMessages(system, user)
        {
            Input = new CleanupInput { Raw = corrected, Options = options, Terms = dictionaryTerms.ToArray(), Context = hasContext ? context : null },
        };
    }
}

/// <summary>The built-in presets' rewrite contract. Separate from <see cref="CleanupPrompt"/> so the strict cleanup rules stay as
/// they are; <see cref="TransformGuard"/> checks the output instead of <see cref="CleanupGuard"/>.</summary>
public static class TransformPrompt
{
    static readonly CleanupPrompt.TransformData T = CleanupPrompt.All.Transform;

    public static readonly string PreviousAIPromptBaseline = T.PreviousAIPromptBaseline;
    public static readonly string AiPromptDetailRule = T.AiPromptDetailRule;
    public static readonly string PreviousDetailAIPromptBaseline = T.PreviousDetailAIPromptBaseline;
    public static readonly string AiPromptConditionRule = T.AiPromptConditionRule;
    public static readonly string PreviousConditionalAIPromptBaseline = T.PreviousConditionalAIPromptBaseline;
    public static readonly string PreviousEmailBaseline = T.PreviousEmailBaseline;
    public static readonly string PreviousNotesBaseline = T.PreviousNotesBaseline;
    public static readonly string PreviousStructuredAIPromptBaseline = T.PreviousStructuredAIPromptBaseline;
    public static readonly string PreviousStructuredEmailBaseline = T.PreviousStructuredEmailBaseline;
    public static readonly string PreviousStructuredNotesBaseline = T.PreviousStructuredNotesBaseline;

    static string Key(ModeTransform kind) => kind switch { ModeTransform.AiPrompt => "aiPrompt", ModeTransform.Email => "email", _ => "notes" };

    /// <summary>The presets' editable instructions.</summary>
    public static string Baseline(ModeTransform kind) => T.Baseline[Key(kind)];

    public static CleanupMessages Build(ModeTransform kind, string transcript, IReadOnlyList<string>? dictionaryTerms = null, string? nonce = null, string? instructions = null)
    {
        var id = PromptTemplate.Id(nonce ?? PromptTemplate.NewNonce());
        var system = PromptTemplate.Fill(T.Frame, ("purpose", T.Purpose[Key(kind)]));
        var terms = (dictionaryTerms ?? []).Select(t => t.Replace("\n", " ")).Where(t => t.Length > 0).Take(50).ToList();
        if (terms.Count > 0) system += PromptTemplate.Fill(T.Terms, ("terms", string.Join(T.TermsSeparator, terms)));
        var notes = ScreenContext.Sanitized(instructions ?? Baseline(kind), WritingModeLimits.Instructions);
        if (notes.Length > 0) system += PromptTemplate.Fill(T.Notes, ("id", id), ("notes", notes));
        return new CleanupMessages(system, PromptTemplate.Fill(T.User, ("id", id), ("transcript", transcript))) { Preset = new PresetInput(kind, transcript, dictionaryTerms ?? []) };
    }
}
