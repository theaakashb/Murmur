// Port of app/WritingModes.swift.
using System.Text.Json.Serialization;

namespace Murmur.Core;

// Writing modes: presets, dictation style, persistence, matching and trigger stripping. The Mac's migrations of older saved modes are not ported
// (Windows starts fresh, spec/settings.md); the preset seeding, the unreadable-list safeguard and everything else match. App identifiers are
// lowercased process names on Windows; the matching logic is the Swift logic.

/// <summary>A user-defined writing mode. Unset overrides inherit the Cleanup settings.</summary>
public sealed record WritingMode
{
    [JsonPropertyName("id"), JsonConverter(typeof(UuidConverter))] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; init; } = "New mode";
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    /// <summary>The focused app's identifier when recording started (a lowercased process name on Windows).</summary>
    [JsonPropertyName("apps")] public IReadOnlyList<string> Apps { get; init; } = [];
    /// <summary>"host", "host/path", "*.host" or "host/path/*"; a subdomain matches its host.</summary>
    [JsonPropertyName("websites")] public IReadOnlyList<string> Websites { get; init; } = [];
    /// <summary>Said at the start or end of a dictation; stripped from the text.</summary>
    [JsonPropertyName("triggers")] public IReadOnlyList<string> Triggers { get; init; } = [];
    /// <summary>Null inherits the Cleanup settings.</summary>
    [JsonPropertyName("options")] public CleanupOptions? Options { get; init; }
    /// <summary>Formal is the default and leaves text as cleanup wrote it.</summary>
    [JsonPropertyName("tone")] public Tone Tone { get; init; } = Tone.Formal;
    /// <summary>Null inherits the global screen-context setting.</summary>
    [JsonPropertyName("screenContext")] public bool? ScreenContext { get; init; }
    /// <summary>Sent to AI cleanup routes only; capped at <see cref="WritingModeLimits.Instructions"/> characters.</summary>
    [JsonPropertyName("instructions")] public string Instructions { get; init; } = "";
    /// <summary>Set on the built-in presets: a separate, explicit rewrite contract on AI routes (never ordinary cleanup). Its triggers match only at the start of a dictation.</summary>
    [JsonPropertyName("transform")] public ModeTransform? Transform { get; init; }

    public bool Equals(WritingMode? other) => other is not null && Id == other.Id && Name == other.Name && Enabled == other.Enabled
        && Apps.SequenceEqual(other.Apps) && Websites.SequenceEqual(other.Websites) && Triggers.SequenceEqual(other.Triggers) && Equals(Options, other.Options)
        && Tone == other.Tone && ScreenContext == other.ScreenContext && Instructions == other.Instructions && Transform == other.Transform;
    public override int GetHashCode() => HashCode.Combine(Id, Name, Enabled, Tone, ScreenContext, Instructions, Transform);

    /// <summary>Seeded once into the user's list; fully editable and deletable afterwards.</summary>
    public static IReadOnlyList<WritingMode> Presets { get; } =
    [
        new() { Id = Guid.Parse("6D1A0001-5E5E-4A4A-9A9A-000000000001"), Name = "Prompt Mode", Triggers = ["prompt mode", "prompt engineer"], Instructions = TransformPrompt.Baseline(ModeTransform.AiPrompt), Transform = ModeTransform.AiPrompt },
        new() { Id = Guid.Parse("6D1A0001-5E5E-4A4A-9A9A-000000000002"), Name = "Email", Triggers = ["email mode"], Instructions = TransformPrompt.Baseline(ModeTransform.Email), Transform = ModeTransform.Email },
        new() { Id = Guid.Parse("6D1A0001-5E5E-4A4A-9A9A-000000000003"), Name = "Notes", Triggers = ["notes mode"], Instructions = TransformPrompt.Baseline(ModeTransform.Notes), Transform = ModeTransform.Notes },
    ];
}

/// <summary>How one dictation is processed: the global settings with the matched mode's overrides.</summary>
public sealed record DictationStyle
{
    public CleanupOptions Options { get; init; }
    public Tone Tone { get; init; } = Tone.Formal;
    public string Instructions { get; init; } = "";
    public ModeTransform? Transform { get; init; }
    /// <summary>A writing mode applied: this dictation runs on the AI engine, not the cleanup route.</summary>
    public bool UsesEngine { get; init; }
    /// <summary>Captured screen text, used only when the mode or the global setting has screen context on and only on an AI route. Null otherwise.</summary>
    public ScreenContext? Context { get; init; }

    public DictationStyle(CleanupOptions options, Tone tone = Tone.Formal, string instructions = "", ScreenContext? context = null)
    {
        Options = options; Tone = tone; Instructions = instructions; Context = context;
    }

    /// <summary>The style for a matched mode (or none). <paramref name="context"/> is dropped unless screen context is on for this mode
    /// (or globally, when the mode does not say). Swift takes a <c>ProcessingSettings</c>; this takes the two fields it reads.</summary>
    public static DictationStyle ForMode(WritingMode? mode, CleanupOptions globalOptions, bool globalScreenContext, ScreenContext? context = null)
    {
        var style = new DictationStyle((mode?.Options ?? globalOptions) with { TrustModel = globalOptions.TrustModel }, mode?.Tone ?? Tone.Formal,
            SwiftText.Prefix(mode?.Instructions ?? "", WritingModeLimits.Instructions), (mode?.ScreenContext ?? globalScreenContext) ? context : null)
        { Transform = mode?.Transform, UsesEngine = mode is not null };
        // Strict and Faithful run on this PC; nothing to send context to.
        return style.UsesModel ? style : style with { Context = null };
    }

    /// <summary>Trigger modes are chosen after transcription, so capture for any enabled candidate that needs it.</summary>
    public static bool NeedsScreenContext(WritingMode? mode, IEnumerable<WritingMode> modes, CleanupOptions globalOptions, bool globalScreenContext)
    {
        bool Needs(WritingMode? m) => (m?.ScreenContext ?? globalScreenContext) && ForMode(m, globalOptions, globalScreenContext).UsesModel;
        return Needs(mode) || modes.Any(m => m.Enabled && m.Triggers.Count > 0 && Needs(m));
    }

    /// <summary>Only the rewrite styles and preset rewrites use the AI model.</summary>
    public bool UsesModel => Options.Polish || Options.Professional || Transform is not null;
}

public static class WritingModes
{
    public const string DefaultsKey = "writingModes";
    public const string PresetsSeededKey = "writingModePresetsSeeded";
    /// <summary>Where an unreadable saved list is kept, so the next save cannot silently replace it with an empty one.</summary>
    public const string UnreadableKey = "writingModesUnreadable";

    /// <summary>The saved modes (stored as JSON text); the first call adds the presets after them (existing modes keep priority), once.</summary>
    public static List<WritingMode> Load(ISettingsStore store)
    {
        var modes = new List<WritingMode>();
        if (store.Get(DefaultsKey) is string saved)
        {
            try { modes = StoreJson.Decode<List<WritingMode>>(System.Text.Encoding.UTF8.GetBytes(saved)); }
            catch (Exception e) when (e is System.Text.Json.JsonException or StoreError) { store.Set(UnreadableKey, saved); }
        }
        if (store.Get(PresetsSeededKey) is not true)
        {
            modes.AddRange(WritingMode.Presets.Where(preset => !modes.Any(m => m.Id == preset.Id)));
            Save(modes, store);
            store.Set(PresetsSeededKey, true);
        }
        return modes;
    }

    public static void Save(IEnumerable<WritingMode> modes, ISettingsStore store) =>
        store.Set(DefaultsKey, System.Text.Encoding.UTF8.GetString(StoreJson.Encode(modes.ToList())));

    /// <summary>The browser URL is read only when an enabled mode lists a website.</summary>
    public static bool NeedsURL(IEnumerable<WritingMode> modes) => modes.Any(m => m.Enabled && m.Websites.Count > 0);

    /// <summary>The mode for the app (and page) focused when recording started: a website match wins over an app match; within a tier the first mode in the list wins.</summary>
    public static WritingMode? Match(IEnumerable<WritingMode> modes, string? appID, Uri? url)
    {
        var enabled = modes.Where(m => m.Enabled).ToList();
        if (url is not null && enabled.FirstOrDefault(m => m.Websites.Any(w => WebsiteMatches(w, url))) is { } site) return site;
        var id = appID?.ToLowerInvariant();
        if (string.IsNullOrEmpty(id)) return null;
        return enabled.FirstOrDefault(m => m.Apps.Any(a => a.ToLowerInvariant() == id));
    }

    static bool IsSwiftWhitespace(char c) => c == '\t' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpaceSeparator;

    /// <summary>"github.com" matches github.com and any subdomain, never "notgithub.com". A path matches itself and anything below it by whole segments;
    /// "/*" and a leading "*." add nothing. Scheme, port, query and case are ignored.</summary>
    public static bool WebsiteMatches(string pattern, Uri url)
    {
        var p = TrimSwiftWhitespace(pattern).ToLowerInvariant();
        foreach (var prefix in new[] { "https://", "http://", "*.", "www." }) if (p.StartsWith(prefix, StringComparison.Ordinal)) p = p[prefix.Length..];
        int slash = p.IndexOf('/');
        if (slash < 0) slash = p.Length;
        var hostPattern = p[..slash].Split(':', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var path = p[slash..];
        while (path.EndsWith('*') || path.EndsWith('/')) path = path[..^1];
        if (hostPattern.Length == 0) return false;
        var host = url.Host.ToLowerInvariant();
        if (host.Length == 0 || !(host == hostPattern || host.EndsWith("." + hostPattern, StringComparison.Ordinal))) return false;
        if (path.Length == 0) return true;
        var urlPath = Uri.UnescapeDataString(url.AbsolutePath).ToLowerInvariant();
        return urlPath == path || urlPath.StartsWith(path + "/", StringComparison.Ordinal);
    }

    static string TrimSwiftWhitespace(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsSwiftWhitespace(s[start])) start++;
        while (end > start && IsSwiftWhitespace(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary>The first enabled mode whose trigger is said at the start or the end, with the trigger removed.</summary>
    public static (WritingMode Mode, string Text)? Trigger(IEnumerable<WritingMode> modes, string transcript)
    {
        foreach (var mode in modes.Where(m => m.Enabled))
        {
            foreach (var phrase in mode.Triggers)
            {
                if (StripTrigger(phrase, transcript, startOnly: mode.Transform is not null) is { } text) return (mode, text);
            }
        }
        return null;
    }

    /// <summary>Whole words only ("code" never matches "codebase"), and only at either end; a dictation that is nothing but the trigger does not match.
    /// "Slack mode, fix it" becomes "Fix it"; "Fix it, slack mode." becomes "Fix it." <paramref name="startOnly"/> (the presets) ignores a trigger said at the end.</summary>
    public static string? StripTrigger(string phrase, string text, bool startOnly = false)
    {
        // "E-mail mode" is one word to the tokenizer, so hyphens are ignored on both sides: "email mode" and "e-mail mode" are the same trigger.
        static string Fold(string word) => Strings.Key(word).Replace("-", "");
        var words = CleanupGuard.Words(phrase).Select(w => Fold(w.Lower)).ToList();
        if (words.Count == 0) return null;
        var t = CleanupText.Tokenize(text);
        var wi = Enumerable.Range(0, t.Count).Where(i => t[i].Kind == TokenKind.Word).ToList();
        if (wi.Count <= words.Count) return null;
        static bool Separator(CleanupToken tok) => tok.Kind == TokenKind.Space || tok.IsComma || tok.IsDash || tok.Text == ":" || tok.Text == ";";
        bool Said(IEnumerable<int> at) => at.Select(i => Fold(t[i].Lower)).SequenceEqual(words);
        if (Said(wi.Take(words.Count)))
        {
            var capital = SwiftText.Characters(t[wi[0]].Text) is { Count: > 0 } first && SwiftText.IsUppercase(first[0]);
            int k = wi[words.Count - 1] + 1;
            if (k < t.Count && t[k].IsTerminal && t[k].Text != "\n") k++; // "Email mode. Fix it."
            while (k < t.Count && (Separator(t[k]) || t[k].Text == "\n")) k++;
            t.RemoveRange(0, k);
            int firstWord = t.FindIndex(x => x.Kind == TokenKind.Word);
            if (capital && firstWord >= 0) t[firstWord] = new CleanupToken(TokenKind.Word, CleanupText.Capitalized(t[firstWord].Text));
            return CleanupText.Render(t);
        }
        if (!startOnly && Said(wi.TakeLast(words.Count)))
        {
            int start = wi[wi.Count - words.Count];
            int end = t.FindLastIndex(x => x.Kind != TokenKind.Space);
            var mark = t[end].IsTerminal && t[end].Text != "\n" ? t[end].Text : "";
            int k = start;
            while (k > 0 && Separator(t[k - 1])) k--;
            // "Fix it. Slack mode." keeps the period that ended the dictated sentence.
            if (k > 0 && t[k - 1].IsTerminal) return CleanupText.Render(t.Take(k));
            return CleanupText.Render(t.Take(k)) + mark;
        }
        return null;
    }
}
