// Port of app/DictationLanguages.swift.
using System.Globalization;

namespace Murmur.Core;

public enum LocalSpeechModel { ParakeetEnglish, ParakeetTDT, WhisperTurbo }

/// <summary>Published model coverage. Language IDs are separate from any flags shown by the picker.</summary>
public sealed class DictationLanguages : IEquatable<DictationLanguages>
{
    // NVIDIA Parakeet TDT v3 model card.
    public static IReadOnlySet<string> Parakeet { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "hr", "hu", "it",
        "lt", "lv", "mt", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "uk",
    };

    // Whisper large-v3-turbo generation_config.json language tokens.
    public static IReadOnlySet<string> Whisper { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "af", "am", "ar", "as", "az", "ba", "be", "bg", "bn", "bo", "br", "bs", "ca",
        "cs", "cy", "da", "de", "el", "en", "es", "et", "eu", "fa", "fi", "fo", "fr",
        "gl", "gu", "ha", "haw", "he", "hi", "hr", "ht", "hu", "hy", "id", "is", "it",
        "ja", "jw", "ka", "kk", "km", "kn", "ko", "la", "lb", "ln", "lo", "lt", "lv",
        "mg", "mi", "mk", "ml", "mn", "mr", "ms", "mt", "my", "ne", "nl", "nn", "no", "oc",
        "pa", "pl", "ps", "pt", "ro", "ru", "sa", "sd", "si", "sk", "sl", "sn", "so",
        "sq", "sr", "su", "sv", "sw", "ta", "te", "tg", "th", "tk", "tl", "tr", "tt",
        "uk", "ur", "uz", "vi", "yi", "yo", "yue", "zh",
    };

    public static IReadOnlySet<string> AvailableCodes { get; } =
        new HashSet<string>(Parakeet.Concat(Whisper), StringComparer.Ordinal);

    const CompareOptions Options =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth;

    static (string Name, string Native) Names(string code, CultureInfo culture)
    {
        string name = code, native;
        try
        {
            var ci = CultureInfo.GetCultureInfo(code);
            var n = culture.Equals(CultureInfo.CurrentUICulture) ? ci.DisplayName : ci.EnglishName;
            if (!string.IsNullOrWhiteSpace(n)) name = n;
            native = string.IsNullOrWhiteSpace(ci.NativeName) ? name : ci.NativeName;
        }
        catch (CultureNotFoundException) { native = name; }
        return (name, native);
    }

    /// <summary>Match the setup language, the language's own name, and its decoder code.</summary>
    public static IReadOnlyList<string> Matching(string query, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        query = query.Trim();
        var cmp = culture.CompareInfo;
        return AvailableCodes
            .Select(code => (code, names: Names(code, culture)))
            .Where(e => query.Length == 0 || new[] { e.code, e.names.Name, e.names.Native }
                .Any(s => cmp.IndexOf(s, query, Options) >= 0))
            .OrderBy(e => e.names.Name, Comparer<string>.Create((a, b) => cmp.Compare(a, b, Options)))
            .ThenBy(e => e.code, StringComparer.Ordinal)
            .Select(e => e.code)
            .ToList();
    }

    public IReadOnlySet<string> Codes { get; }

    DictationLanguages(HashSet<string> codes) { Codes = codes; }

    /// <summary>Null when empty or when any code is unsupported.</summary>
    public static DictationLanguages? Create(IEnumerable<string> codes)
    {
        var set = new HashSet<string>(codes, StringComparer.Ordinal);
        if (set.Count == 0 || !set.IsSubsetOf(AvailableCodes)) return null;
        return new DictationLanguages(set);
    }

    /// <summary>An absent preference represents an existing English-only setup.</summary>
    public static DictationLanguages English { get; } = Create(new[] { "en" })!;

    /// <summary>Prefer the smallest model that covers the complete selection.</summary>
    public LocalSpeechModel LocalModel =>
        Equals(English) ? LocalSpeechModel.ParakeetEnglish
        : Codes.IsSubsetOf(Parakeet) ? LocalSpeechModel.ParakeetTDT
        : LocalSpeechModel.WhisperTurbo;

    public string? CloudHint => Equals(English) ? "en" : null;

    public bool Equals(DictationLanguages? other) => other is not null && Codes.SetEquals(other.Codes);
    public override bool Equals(object? obj) => Equals(obj as DictationLanguages);
    public override int GetHashCode() => Codes.Aggregate(0, (h, c) => h ^ StringComparer.Ordinal.GetHashCode(c));
}
