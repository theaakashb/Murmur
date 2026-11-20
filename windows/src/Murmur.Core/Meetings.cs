// Port of app/Meetings.swift.
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Murmur.Core;

public sealed record MeetingLine(int Id, MeetingStream Stream, double Start, double End, string Text)
{
    public string? Shown { get; set; }
    public bool? LocalFallback { get; init; }
    public int? Speaker { get; set; }
    public double[][]? Words { get; set; }
    [JsonIgnore] public bool IsHidden => Shown == "";
    [JsonIgnore] public string VisibleText => Shown ?? Text;
}

public sealed partial record Meeting : IRetainedHistoryItem
{
    public required string Id { get; init; }
    [JsonConverter(typeof(SwiftDateConverter))] public required DateTime RecordedAt { get; init; }
    public required MeetingSource Source { get; set; }
    public double Seconds { get; set; }
    public List<MeetingLine> Lines { get; set; } = [];
    public string MyNotes { get; set; } = "";
    public List<double> Gaps { get; init; } = [];
    public bool Finished { get; set; }
    public string? TranscribedBy { get; init; }
    public MeetingNotes? Notes { get; set; }
    public MeetingNotes? LectureNotes { get; set; }
    public SpeakerSeparation? Speakers { get; set; }

    [JsonIgnore] public string Title => Notes?.Title ?? DefaultTitle;
    [JsonIgnore] public string DefaultTitle
    {
        get
        {
            var date = RecordedAt.ToLocalTime();
            var label = Source.MicOnly ? "Meeting notes" : Source.AllApps ? "Meeting" : $"Meeting with {Source.Name}";
            return $"{label}, {date:d} {date:t}";
        }
    }

    public bool Matches(string query, DateTime? from = null, DateTime? through = null, TimeZoneInfo? zone = null)
    {
        var date = TimeZoneInfo.ConvertTimeFromUtc(SwiftDate.Normalize(RecordedAt), zone ?? TimeZoneInfo.Local).Date;
        if (from is { } start && date < start.Date || through is { } end && date > end.Date) return false;
        query = SwiftText.TrimWhitespaceAndNewlines(query);
        if (query.Length == 0) return true;
        var needle = SearchText(query);
        var details = new[] { Notes, LectureNotes }.OfType<MeetingNotes>().SelectMany(notes =>
            new[] { notes.Title, notes.Summary }.Concat(notes.KeyPoints.Concat(notes.Decisions).Concat(notes.Actions)
                .Select(item => string.Join(" · ", new[] { item.Text, item.Owner, item.Due }.Where(value => !string.IsNullOrEmpty(value))))));
        return details.Concat(Lines.Select(line => line.VisibleText)).Concat([Title, Source.Name, MyNotes]).OfType<string>()
            .Any(text => SearchText(text).Contains(needle, StringComparison.Ordinal));
    }

    static string SearchText(string text)
    {
        // Invariant globalization does not implement CompareOptions.IgnoreNonSpace.
        var output = new StringBuilder(text.Length);
        foreach (int scalar in SwiftText.CanonicalDecomposition(SwiftText.Lowercased(text)))
        {
            var rune = new Rune(scalar);
            if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark))
                output.Append(rune);
        }
        return output.ToString();
    }

    public void DropWordTimes() { foreach (var line in Lines) line.Words = null; }

    public void MarkNotesOutdated(bool meetingNotes = true)
    {
        if (LectureNotes is { } lecture) LectureNotes = lecture with { Outdated = true };
        if (meetingNotes && Notes is { } notes) Notes = notes with { Outdated = true };
    }

    public void Insert(MeetingStream stream, double start, double end, string text, bool localFallback = false, double[][]? words = null)
    {
        text = SwiftText.TrimWhitespaceAndNewlines(text);
        if (text.Length == 0) return;
        var line = new MeetingLine(Lines.Select(item => item.Id).DefaultIfEmpty().Max() + 1, stream, start, end, text)
        {
            LocalFallback = localFallback ? true : null,
            Words = words?.Length == MeetingEcho.Split(text).Length ? words : null,
        };
        int index = Lines.FindIndex(item => item.Start > start || item.Start == start && item.Stream == MeetingStream.Them && stream == MeetingStream.Me);
        Lines.Insert(index < 0 ? Lines.Count : index, line);
        Seconds = Math.Max(Seconds, end);
        MeetingEcho.Apply(Lines, start - MeetingEcho.Window - 30, end + MeetingEcho.Window + 30);
    }
}

public static class MeetingEcho
{
    public const int MinimumRun = 3;
    public const double Window = 3;
    internal static string[] Split(string text)
    {
        var words = new List<string>();
        int start = 0, index = 0;
        foreach (var c in SwiftText.Characters(text))
        {
            if (SwiftText.IsWhitespace(c))
            {
                if (start < index) words.Add(text[start..index]);
                start = index + c.Length;
            }
            index += c.Length;
        }
        if (start < index) words.Add(text[start..index]);
        return words.ToArray();
    }

    static string[] Words(string text) => Split(SwiftText.Lowercased(text))
        .Select(word => string.Concat(SwiftText.Characters(word).Where(c => SwiftText.IsLetter(c) || SwiftText.IsNumber(c)))).ToArray();

    public static string? Shown(string text, IEnumerable<string> them)
    {
        var mine = Words(text);
        var theirs = them.Select(Words).Where(words => words.Length > 0).ToArray();
        var hidden = new bool[mine.Length];
        for (int start = 0; start < mine.Length; start++)
        {
            if (mine[start].Length == 0) continue;
            foreach (var other in theirs)
            for (int j = 0; j < other.Length; j++)
            {
                int length = 0;
                while (start + length < mine.Length && j + length < other.Length &&
                    mine[start + length].Length > 0 && SwiftText.CharEquals(mine[start + length], other[j + length])) length++;
                if (length >= MinimumRun) Array.Fill(hidden, true, start, length);
            }
        }
        return hidden.Contains(true) ? string.Join(' ', Split(text).Where((_, index) => !hidden[index])) : null;
    }

    public static void Apply(IEnumerable<MeetingLine> lines, double from, double to)
    {
        var them = lines.Where(line => line.Stream == MeetingStream.Them).ToArray();
        foreach (var me in lines.Where(line => line.Stream == MeetingStream.Me && line.Start >= from && line.Start <= to))
            me.Shown = Shown(me.Text, them.Where(line => line.End >= me.Start - Window && line.Start <= me.End + Window).Select(line => line.Text));
    }
}

public sealed record ChunkText(string Text, bool LocalFallback = false, double[][]? Words = null);

public enum MeetingTranscription { Local, Cloud }
public abstract record MeetingCloudResult
{
    public sealed record Text(string Value) : MeetingCloudResult;
    public sealed record NoSpeech : MeetingCloudResult;
    public sealed record Failed : MeetingCloudResult;
}

public static class MeetingRoute
{
    public static MeetingTranscription Load(ISettingsStore settings) =>
        settings.Get("meetingTranscription") as string == "cloud" ? MeetingTranscription.Cloud : MeetingTranscription.Local;

    public static Func<string, CancellationToken, Task<ChunkText?>> Transcriber(
        Func<string, CancellationToken, Task<MeetingCloudResult>>? cloud, Func<string, CancellationToken, Task<ChunkText?>> local) =>
        async (path, token) =>
        {
            if (cloud is null) return await local(path, token).ConfigureAwait(false);
            return await cloud(path, token).ConfigureAwait(false) switch
            {
                MeetingCloudResult.Text text => new(text.Value),
                MeetingCloudResult.NoSpeech => new(""),
                _ => await local(path, token).ConfigureAwait(false) is { } fallback ? fallback with { LocalFallback = true } : null,
            };
        };

    public const double MegabytesPerStreamHour = 115.2;
    public static string CloudNote(string provider, string host) =>
        $"Meetings send all of their audio, including other participants’ voices, to {provider} at {host}: up to about 230 MB an hour with both streams. Long meetings can cost significantly. The price depends on the provider and model. If a part fails, it is transcribed on this PC instead and marked. The meeting stays on the cloud route.";
}

public sealed class StreamWatch(double now)
{
    public const double StallAfter = 3, RebuildEvery = 3;
    public enum Action { None, Stalled, Rebuild, Recovered }
    long lastCount;
    double lastProgress = now, lastRebuild = double.NegativeInfinity;
    public double? StalledSince { get; private set; }

    public (Action Kind, double After) Update(long count, double now, bool idle = false)
    {
        if (count > lastCount || idle)
        {
            lastCount = Math.Max(lastCount, count);
            lastProgress = now;
            if (StalledSince is not { } since) return (Action.None, 0);
            StalledSince = null;
            return (Action.Recovered, now - since);
        }
        if (now - lastProgress < StallAfter) return (Action.None, 0);
        if (StalledSince is not null) return (now - lastRebuild >= RebuildEvery ? Action.Rebuild : Action.None, 0);
        StalledSince = lastProgress;
        return (Action.Stalled, 0);
    }

    public void Rebuilt(double now) { lastCount = 0; lastRebuild = now; }
}

public sealed class MeterScale
{
    public const double Floor = 0.05, Decay = 0.97;
    double reference = Floor;
    public double Display(double level)
    {
        reference = Math.Max(level, Math.Max(reference * Decay, Floor));
        return Math.Min(1, Math.Sqrt(Math.Max(0, level) / reference));
    }
}

public static class MeetingLimits
{
    public const double TickInterval = 0.15, Cap = 3 * 3600, Warning = Cap - 600, AskEnded = 300, StopSilent = 600, Behind = 120, SilenceLevel = 0.02;
    public enum Action { None, WarnCap, StopCap, AskEnded, StopSilent }
    public static Action Check(double elapsed, double silentFor) =>
        elapsed >= Cap ? Action.StopCap : silentFor >= StopSilent ? Action.StopSilent :
        elapsed >= Warning ? Action.WarnCap : silentFor >= AskEnded ? Action.AskEnded : Action.None;
}

public static class MeetingConsent
{
    public const int Version = 1;
    const string Key = "meetingConsentVersion";
    public static bool Needed(ISettingsStore settings) => new Setting<int>(Key, 0).Read(settings.Get(Key)) < Version;
    public static void Acknowledge(ISettingsStore settings) => settings.Set(Key, Version);
}

public static class MeetingQuit
{
    public sealed record Warning(string Title, string Detail, string Confirm);
    public static Warning? Check(bool recording, int unsaved)
    {
        if (unsaved > 0)
        {
            var many = unsaved > 1;
            var pronoun = many ? "them" : "it";
            return new(many ? $"{unsaved} meetings have not been saved" : "A meeting has not been saved",
                $"Murmur could not write {pronoun} to disk, so quitting now loses {pronoun}. Go back to copy the notes or transcript from Meetings, or free up disk space and let Murmur try again."
                + (recording ? " The meeting recording now would also stop." : ""), many ? "Quit and Lose Them" : "Quit and Lose It");
        }
        return recording ? new("A meeting is still recording",
            $"Quitting stops the recording. What was recorded so far is saved, and any part not yet transcribed is kept for {RetryWindow.Text}.",
            "Stop and Quit") : null;
    }
}
