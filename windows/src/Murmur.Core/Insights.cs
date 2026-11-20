// Port of app/Insights.swift.
using System.Globalization;

namespace Murmur.Core;

public sealed record InsightsItem(DateTimeOffset RecordedAt, string RawText, string FinalText, double? Seconds, string? App, bool Expanded = false)
{
    public static InsightsItem FromHistory(HistoryEntry entry) =>
        new(entry.RecordedAt, entry.RawText, entry.FinalText, entry.Seconds, entry.App, entry.Cues is not null);
}

public abstract record Insights
{
    public sealed record HistoryOff : Insights;
    public sealed record Empty : Insights;
    public sealed record Stats : Insights
    {
        public double? RecordingSeconds { get; internal set; }
        public Dictionary<DateOnly, int> DailyWords { get; } = [];
        public int[] HourlyDictations { get; } = new int[24];
        public double AverageWords => Dictations > 0 ? (double)Words / Dictations : 0;
        public int Dictations { get; internal set; }
        public int Words { get; internal set; }
        public double? WordsPerMinute { get; internal set; }
        public double? SecondsSaved { get; internal set; }
        public int TimedDictations { get; internal set; }
        public int WordsChanged { get; internal set; }
        public IReadOnlyList<AppCount> Apps { get; internal set; } = [];
        public int WithoutApp { get; internal set; }
        public IReadOnlySet<DateOnly> Days { get; internal set; } = new HashSet<DateOnly>();
        public int CurrentStreak { get; internal set; }
        public int LongestStreak { get; internal set; }
    }

    public sealed record AppCount(string App, int Count, int Words = 0);

    // Dhakal et al., CHI 2018 (doi:10.1145/3173574.3174220): five-character words per minute.
    public const double TypingWPM = 51.56;

    public static Insights Compute(IEnumerable<InsightsItem> items, bool historyOn, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        if (!historyOn) return new HistoryOff();
        var stats = new Stats();
        double spoken = 0, seconds = 0, saved = 0;
        var apps = new Dictionary<string, AppCount>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var final = SwiftText.TrimWhitespaceAndNewlines(item.FinalText);
            if (final.Length == 0) continue;
            var local = TimeZoneInfo.ConvertTime(item.RecordedAt, timeZone);
            var day = DateOnly.FromDateTime(local.DateTime);
            stats.Dictations++;
            var words = SplitWords(final).Count;
            stats.Words += words;
            stats.DailyWords[day] = stats.DailyWords.GetValueOrDefault(day) + words;
            stats.HourlyDictations[local.Hour]++;
            if (!item.Expanded) stats.WordsChanged += ChangedWords(item.RawText, final);
            if (item.Seconds is > 0 and var length && double.IsFinite(length))
            {
                stats.TimedDictations++;
                spoken += StandardWords(item.RawText);
                seconds += length;
                saved += StandardWords(final) / TypingWPM * 60 - length;
            }
            if (item.App is { Length: > 0 } app)
            {
                var key = Strings.Key(app);
                var count = apps.GetValueOrDefault(key) ?? new AppCount(app, 0);
                apps[key] = count with { Count = count.Count + 1, Words = count.Words + words };
            }
            else stats.WithoutApp++;
        }

        if (stats.Dictations == 0) return new Empty();
        if (seconds > 0)
        {
            stats.RecordingSeconds = seconds;
            stats.WordsPerMinute = spoken / (seconds / 60);
            stats.SecondsSaved = Math.Max(0, saved);
        }
        stats.Apps = apps.Values.OrderByDescending(a => a.Words).ThenBy(a => Strings.Key(a.App), StringComparer.Ordinal).ToArray();
        stats.Days = stats.DailyWords.Keys.ToHashSet();
        (stats.CurrentStreak, stats.LongestStreak) = Streaks(stats.Days, now, timeZone);
        return stats;
    }

    public static IReadOnlyList<string> SplitWords(string text) => Strings.Split(text, SwiftText.IsWhitespace);

    public static double StandardWords(string text) => SwiftText.Count(SwiftText.TrimWhitespaceAndNewlines(text)) / 5.0;

    public static int ChangedWords(string raw, string final)
    {
        var (removed, inserted) = WordDifference.Of(SplitWords(raw).Select(Strings.Key).ToArray(), SplitWords(final).Select(Strings.Key).ToArray());
        return Math.Max(removed.Count, inserted.Count);
    }

    public static (int Current, int Longest) Streaks(IReadOnlySet<DateOnly> days, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        int longest = 0, run = 0, previous = -2;
        foreach (var day in days.Order())
        {
            run = previous + 1 == day.DayNumber ? run + 1 : 1;
            longest = Math.Max(longest, run);
            previous = day.DayNumber;
        }
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        int current = 0;
        for (int cursor = today.DayNumber - (days.Contains(today) ? 0 : 1);
             cursor >= 0 && days.Contains(DateOnly.FromDayNumber(cursor)); cursor--)
            current++;
        return (current, longest);
    }

    public static string Duration(double seconds)
    {
        var total = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
        if (total < 60) return string.Create(CultureInfo.InvariantCulture, $"{total} s");
        var minutes = total / 60;
        return minutes < 60 ? string.Create(CultureInfo.InvariantCulture, $"{minutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h {minutes % 60} min");
    }
}
