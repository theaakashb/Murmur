// Port of app/ProfessionalHint.swift.
using System.Text.Json;

namespace Murmur.Core;

public static class ProfessionalSignal
{
    public enum Reason { RunOn, Correction, List, Fillers }
    public const int RunOnWords = 35, LongRunOnWords = 70;
    public const double FillerShare = 0.08;

    sealed record Data(string[] CorrectionCues, string[] ListWords, string[] Fillers, Dictionary<string, string> SentenceParts);
    static readonly Data Text = SharedData.Load<Data>("professional_hint");

    public static IReadOnlyList<string> Words(string text) =>
        Strings.Split(SwiftText.Lowercased(text), c => !SwiftText.IsLetter(c) && !SwiftText.IsNumber(c) && c != "'");

    public static int LongestRun(string text)
    {
        int best = 0, run = 0;
        foreach (var word in Insights.Words(text))
        {
            best = Math.Max(best, ++run);
            if (SwiftText.Characters(word)[^1] is "." or "?" or "!") run = 0;
        }
        return best;
    }

    public static IReadOnlyList<Reason> Reasons(string raw)
    {
        var words = Words(raw);
        var joined = " " + string.Join(" ", words) + " ";
        var found = new List<Reason>();
        var run = LongestRun(raw);
        if (run >= RunOnWords) found.Add(Reason.RunOn);
        if (Text.CorrectionCues.Any(c => Strings.Contains(joined, " " + c + " "))) found.Add(Reason.Correction);
        if (words.Count(Text.ListWords.Contains) >= 2) found.Add(Reason.List);
        var filler = words.Count(Text.Fillers.Contains);
        if (filler >= 3 && (double)filler / Math.Max(1, words.Count) >= FillerShare) found.Add(Reason.Fillers);
        return found.Count >= 2 || run >= LongRunOnWords ? found : [];
    }

    public static string Sentence(IEnumerable<Reason> reasons)
    {
        var parts = reasons.Select(r => Text.SentenceParts[JsonNamingPolicy.CamelCase.ConvertName(r.ToString())]).ToArray();
        if (parts.Length == 0) return "";
        return "Professional would " + (parts.Length == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1]) + ".";
    }
}

public sealed record ProfessionalHintState
{
    public int Dictations { get; set; }
    public int Dismissals { get; set; }
    public DateTimeOffset? FirstSeen { get; set; }
    public DateTimeOffset? LastOffered { get; set; }
    public string? OfferID { get; set; }
    public bool Acted { get; set; }

    public const int MinDictations = 10, MinDays = 3, MinGapDays = 7, MaxDismissals = 2;

    public void Consider(string entryID, string raw, DateTimeOffset now, bool enabled, bool professionalActive)
    {
        Dictations++;
        FirstSeen ??= now;
        if (!enabled || professionalActive || Acted || Dismissals >= MaxDismissals || OfferID is not null ||
            Dictations < MinDictations || (now - FirstSeen.Value).TotalDays < MinDays ||
            (LastOffered is { } last && (now - last).TotalDays < MinGapDays) ||
            ProfessionalSignal.Reasons(raw).Count == 0) return;
        OfferID = entryID;
        LastOffered = now;
    }
}

/// <summary>Persists only the offer ID, counters, dates and user choices. Drive from the app's UI thread.</summary>
public sealed class ProfessionalHint(ISettingsStore settings)
{
    public const string StateKey = "professionalHintState", EnabledKey = "professionalHints";
    public ProfessionalHintState State => Load(settings);
    public bool Enabled => settings.Get(EnabledKey) as bool? ?? true;
    public void SetEnabled(bool enabled) => settings.Set(EnabledKey, enabled);

    static ProfessionalHintState Load(ISettingsStore settings)
    {
        if (settings.Get(StateKey) is not JsonElement stored) return new();
        try { return stored.Deserialize<ProfessionalHintState>(SharedData.JsonOptions) ?? new(); }
        catch (JsonException) { return new(); }
    }

    void Save(ProfessionalHintState state) => settings.Set(StateKey, JsonSerializer.SerializeToElement(state, SharedData.JsonOptions));

    public void Consider(string entryID, string raw, DateTimeOffset now, bool professionalActive)
    {
        var state = State;
        state.Consider(entryID, raw, now, Enabled, professionalActive);
        Save(state);
    }

    public void Dismiss()
    {
        var state = State;
        Save(state with { Dismissals = state.Dismissals + 1, OfferID = null });
    }

    public void Finish() => Save(State with { Acted = true, OfferID = null });
}
