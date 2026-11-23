// Port of the pure parts of app/DictationLanguagePicker.swift: the suggested list, flags and the selection rules.
namespace Murmur.Core;

public static class DictationLanguagePickerModel
{
    /// <summary>Shown first while the search box is empty.</summary>
    public static readonly IReadOnlyList<string> Suggested = ["en", "es", "fr", "hi", "ar"];

    static readonly Dictionary<string, string> Regions = new()
    {
        ["ar"] = "SA", ["bg"] = "BG", ["cs"] = "CZ", ["da"] = "DK", ["de"] = "DE", ["el"] = "GR", ["en"] = "US",
        ["es"] = "ES", ["et"] = "EE", ["fi"] = "FI", ["fr"] = "FR", ["hi"] = "IN", ["hr"] = "HR", ["hu"] = "HU",
        ["it"] = "IT", ["ja"] = "JP", ["ko"] = "KR", ["lt"] = "LT", ["lv"] = "LV", ["mt"] = "MT", ["nl"] = "NL",
        ["pl"] = "PL", ["pt"] = "PT", ["ro"] = "RO", ["ru"] = "RU", ["sk"] = "SK", ["sl"] = "SI", ["sv"] = "SE",
        ["tr"] = "TR", ["uk"] = "UA", ["ur"] = "PK", ["vi"] = "VN", ["zh"] = "CN",
    };

    /// <summary>The regional-indicator flag for the language's usual region, or a globe.</summary>
    public static string Flag(string code) =>
        Regions.TryGetValue(code, out var region)
            ? string.Concat(region.Select(c => char.ConvertFromUtf32(c + 127397)))
            : "\U0001F310";

    /// <summary>With an empty search the suggested languages come first, then the rest in the given order; otherwise the matches as they are.</summary>
    public static IReadOnlyList<string> Results(string query, IReadOnlyList<string> matches) =>
        string.IsNullOrWhiteSpace(query) ? [.. Suggested, .. matches.Where(m => !Suggested.Contains(m))] : matches;

    /// <summary>The selection after a checkbox change: adding appends; removing is ignored when it would leave nothing.</summary>
    public static List<string> Toggle(IReadOnlyList<string> languages, string code, bool selected)
    {
        var next = languages.ToList();
        if (selected) { if (!next.Contains(code)) next.Add(code); }
        else if (next.Count > 1) next.RemoveAll(c => c == code);
        return next;
    }

    /// <summary>The only selected language cannot be unchecked.</summary>
    public static bool IsLocked(IReadOnlyList<string> languages, string code) => languages.Count == 1 && languages[0] == code;
}
