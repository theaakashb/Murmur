// Port of app/Tips.swift.
using System.Globalization;
using System.Text.Json;

namespace Murmur.Core;

/// <summary>Settings-backed tip ledger. Counters and dates only. Drive from the app's UI thread.</summary>
public sealed class Tips(ISettingsStore settings)
{
    public enum ID { Milestone, VoiceEdit, Modes, Dictionary, Cues }
    public enum Pane { Modes = 3, Dictionary = 4, Cues = 11 }
    public sealed record Milestone(int Words, double SecondsSaved);
    public sealed record Usage(int Dictations, int Apps, int LearnedWords, int DaysUsed);

    public const string EnabledKey = "tips.enabled", LastShownKey = "tips.lastShown", DismissalsKey = "tips.dismissals", DoneKey = "tips.done";
    public const string WordsKey = "tips.lifetimeWords", SavedKey = "tips.lifetimeSecondsSaved", RungKey = "tips.milestoneRung";
    public const string HomeSeenKey = "tips.homeDismissedRung", RewritesKey = "tips.rewrites", FirstUseKey = "tips.firstUse", OpenedKey = "tips.openedPanes";
    public static readonly IReadOnlyList<string> AllKeys = [EnabledKey, LastShownKey, DismissalsKey, DoneKey, WordsKey, SavedKey, RungKey, HomeSeenKey, RewritesKey, FirstUseKey, OpenedKey];

    sealed record Data(int[] Rungs);
    public static IReadOnlyList<int> Rungs { get; } = Array.AsReadOnly(SharedData.Load<Data>("tips").Rungs);
    static readonly (ID Id, Pane Pane)[] DotPanes = [(ID.Modes, Pane.Modes), (ID.Dictionary, Pane.Dictionary), (ID.Cues, Pane.Cues)];

    static string Raw(ID id) => JsonNamingPolicy.CamelCase.ConvertName(id.ToString());
    int Int(string key) => new Setting<int>(key, 0).Read(settings.Get(key));
    IReadOnlyList<string> List(string key) => settings.Get(key) as IReadOnlyList<string> ?? [];
    static DateTimeOffset? Date(object? stored) => stored is string text &&
        DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
    static string StoreDate(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public bool Enabled => settings.Get(EnabledKey) as bool? ?? true;
    public void SetEnabled(bool on) => settings.Set(EnabledKey, on);

    Dictionary<string, int> Dismissals()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (settings.Get(DismissalsKey) is JsonElement { ValueKind: JsonValueKind.Object } obj)
            foreach (var entry in obj.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetInt32(out int count))
                    result[entry.Name] = count;
        return result;
    }

    public bool IsDone(ID id) => List(DoneKey).Contains(Raw(id));

    public bool CanShow(ID id, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        if (!Enabled || IsDone(id) || Dismissals().GetValueOrDefault(Raw(id)) >= 2) return false;
        if (Date(settings.Get(LastShownKey)) is not { } last) return true;
        timeZone ??= TimeZoneInfo.Local;
        return TimeZoneInfo.ConvertTime(last, timeZone).Date != TimeZoneInfo.ConvertTime(now, timeZone).Date;
    }

    public void Shown(DateTimeOffset now) => settings.Set(LastShownKey, StoreDate(now));

    public void Dismissed(ID id)
    {
        var all = Dismissals();
        var key = Raw(id);
        all[key] = all.GetValueOrDefault(key) + 1;
        settings.Set(DismissalsKey, JsonSerializer.SerializeToElement(all));
    }

    public void Completed(ID id)
    {
        if (!IsDone(id)) settings.Set(DoneKey, List(DoneKey).Append(Raw(id)).ToArray());
    }

    public void Reset()
    {
        foreach (string key in new[] { LastShownKey, DismissalsKey, DoneKey, HomeSeenKey, OpenedKey }) settings.Remove(key);
        SetEnabled(true);
    }

    public int LifetimeWords => Int(WordsKey);
    public double LifetimeSecondsSaved => new Setting<double>(SavedKey, 0).Read(settings.Get(SavedKey));
    public int Rung => Int(RungKey);

    public Milestone? Record(int words, double secondsSaved, DateTimeOffset now, Func<(int Words, double SecondsSaved, DateTimeOffset? FirstUse)> seed)
    {
        if (settings.Get(WordsKey) is null)
        {
            var start = seed();
            var passed = Rungs.LastOrDefault(r => r <= start.Words);
            settings.Set(SavedKey, Math.Max(0, start.SecondsSaved));
            settings.Set(RungKey, passed);
            settings.Set(HomeSeenKey, passed);
            settings.Set(FirstUseKey, StoreDate(start.FirstUse ?? now));
            settings.Set(WordsKey, start.Words);
        }
        var total = LifetimeWords + Math.Max(0, words);
        var saved = Math.Max(0, LifetimeSecondsSaved + secondsSaved);
        settings.Set(WordsKey, total);
        settings.Set(SavedKey, saved);
        var reached = Rungs.LastOrDefault(r => r <= total);
        if (reached <= Rung) return null;
        settings.Set(RungKey, reached);
        return new(reached, saved);
    }

    public static double SecondsSaved(string final, double? seconds) => seconds is > 0 and var length && double.IsFinite(length)
        ? Insights.StandardWords(final) / Insights.TypingWPM * 60 - length : 0;

    public static int? Next(int after) => Rungs.Where(r => r > after).Select(r => (int?)r).FirstOrDefault();

    public int? HomeMilestone => Enabled && Rung > Math.Max(0, Int(HomeSeenKey)) ? Rung : null;
    public void DismissHome() => settings.Set(HomeSeenKey, Rung);

    public static string Duration(double seconds)
    {
        var minutes = Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (minutes < 60) return minutes == 1 ? "1 minute" : string.Create(CultureInfo.InvariantCulture, $"{minutes} minutes");
        var hours = Math.Round(seconds / 3600 * 2, MidpointRounding.AwayFromZero) / 2;
        return hours == 1 ? "1 hour" : string.Create(CultureInfo.InvariantCulture, $"{hours:0.#} hours");
    }

    public static bool IsRewrite(string initial, string edited)
    {
        var count = Insights.SplitWords(initial).Count;
        return count >= 4 && Insights.ChangedWords(initial, edited) >= Math.Max(3, (int)Math.Ceiling(count * 0.3));
    }

    public bool NoteRewrite(DateTimeOffset now)
    {
        var week = now.AddDays(-7);
        var recent = List(RewritesKey).Where(s => Date(s) > week).Append(StoreDate(now)).ToArray();
        settings.Set(RewritesKey, recent);
        return recent.Length >= 2;
    }

    public IReadOnlyList<Pane> Eligible(Usage usage)
    {
        if (!Enabled) return [];
        return DotPanes.Where(pair => !IsDone(pair.Id) && pair.Id switch
        {
            ID.Modes => usage.Dictations >= 20 && usage.Apps >= 2,
            ID.Dictionary => usage.LearnedWords >= 1,
            ID.Cues => usage.DaysUsed >= 7,
            _ => false,
        }).Select(pair => pair.Pane).ToArray();
    }

    IReadOnlyList<double> OpenedPanes => settings.Get(OpenedKey) as IReadOnlyList<double> ?? [];

    public IReadOnlySet<Pane> Dots(Usage usage)
    {
        var opened = OpenedPanes;
        return Eligible(usage).Where(pane => !opened.Contains((int)pane)).Take(2).ToHashSet();
    }

    public void Opened(Pane pane)
    {
        var opened = OpenedPanes;
        if (!opened.Contains((int)pane)) settings.Set(OpenedKey, opened.Append((double)pane).ToArray());
    }

    public static ID? IdFor(Pane pane) => DotPanes.Where(pair => pair.Pane == pane).Select(pair => (ID?)pair.Id).FirstOrDefault();

    public int DaysUsed(DateTimeOffset now) => Date(settings.Get(FirstUseKey)) is { } first ? (int)(now - first).TotalDays : 0;
}
