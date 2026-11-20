// Port of the pure logic in app/MeetingSpeakers.swift.
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Murmur.Core;

public sealed record SpeakerSegment(string Speaker, double Start, double End);

[JsonConverter(typeof(JsonStringEnumConverter<SpeakerState>))]
public enum SpeakerState
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("done")] Done,
    [JsonStringEnumMemberName("skipped")] Skipped,
    [JsonStringEnumMemberName("failed")] Failed,
}

public sealed record SpeakerSeparation(SpeakerState State)
{
    public Dictionary<int, string> Names { get; init; } = [];
    public double? Threshold { get; init; }
    [JsonConverter(typeof(SwiftDateConverter))] public DateTime? EditedAt { get; init; }
    public string? Failure { get; init; }
}

public static class MeetingSpeakers
{
    public const double Threshold = 0.6;
    public static string Label(MeetingLine line, Meeting meeting) =>
        line.Speaker is { } number && line.Stream == meeting.Source.SpeakerStream ? Label(number, meeting)
            : meeting.Source.MicOnly ? "Room" : line.Stream == MeetingStream.Me ? "Me" : "Them";

    public static string Label(int number, Meeting meeting) => meeting.Speakers?.Names.GetValueOrDefault(number) ?? $"Speaker {number}";
    public static int[] Numbers(Meeting meeting) => meeting.Lines.Where(line => line.Speaker.HasValue).Select(line => line.Speaker!.Value).Distinct().Order().ToArray();

    public static string Replace(string label, string replacement, string text) => label.Length == 0 ? text :
        Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(label) + @"(?![\p{L}\p{N}])",
            _ => replacement, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string? Speaker(double start, double end, IReadOnlyList<SpeakerSegment> segments, bool nearest)
    {
        var overlaps = new Dictionary<string, double>();
        foreach (var segment in segments)
        {
            double shared = Math.Min(end, segment.End) - Math.Max(start, segment.Start);
            if (shared > 0) overlaps[segment.Speaker] = overlaps.GetValueOrDefault(segment.Speaker) + shared;
        }
        if (overlaps.Count > 0) return overlaps.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal).First().Key;
        return nearest ? segments.MinBy(segment => Math.Max(0, Math.Max(segment.Start - end, start - segment.End)))?.Speaker : null;
    }

    public static void Apply(IReadOnlyList<SpeakerSegment> segments, Meeting meeting)
    {
        int nextId = meeting.Lines.Select(line => line.Id).DefaultIfEmpty().Max() + 1;
        var output = new List<(MeetingLine Line, string? Speaker)>();
        foreach (var line in meeting.Lines)
        {
            if (line.Stream != meeting.Source.SpeakerStream) { output.Add((line with { Words = null }, null)); continue; }
            int index = 0;
            foreach (var piece in Split(line, segments))
            {
                output.Add((piece.Line with { Id = index++ == 0 ? line.Id : nextId++, Words = null }, piece.Speaker));
            }
        }
        var numbers = new Dictionary<string, int>();
        meeting.Lines = output.OrderBy(piece => piece.Line.Start).ThenBy(piece => piece.Line.Stream).Select(piece =>
        {
            if (piece.Speaker is not { } speaker) return piece.Line with { Speaker = null };
            numbers.TryAdd(speaker, numbers.Count + 1);
            return piece.Line with { Speaker = numbers[speaker] };
        }).ToList();
        meeting.Speakers = (meeting.Speakers ?? new(SpeakerState.Done)) with { State = SpeakerState.Done };
    }

    static IEnumerable<(MeetingLine Line, string? Speaker)> Split(MeetingLine line, IReadOnlyList<SpeakerSegment> segments)
    {
        var words = MeetingEcho.Split(line.Text);
        var times = line.Words;
        if (times is not null && times.Length == words.Length && words.Length > 0 && times.All(time => time is { Length: 2 }))
        {
            var speakers = times.Select(time =>
            {
                double mid = line.Start + (time[0] + time[1]) / 2;
                return Speaker(mid - 0.05, mid + 0.05, segments, false) ??
                    Speaker(line.Start + time[0], line.Start + time[1], segments, false);
            }).ToArray();

            if (speakers.FirstOrDefault(speaker => speaker is not null) is { } previous)
            {
                for (int i = 0; i < speakers.Length; i++) previous = speakers[i] = speakers[i] ?? previous;
                int first = 0;
                for (int i = 1; i <= words.Length; i++)
                {
                    if (i < words.Length && speakers[i] == speakers[first]) continue;
                    double start = first == 0 ? line.Start : line.Start + times[first][0];
                    double end = i == words.Length ? line.End : Math.Max(start, line.Start + times[i - 1][1]);
                    yield return (new(line.Id, line.Stream, start, end, string.Join(' ', words[first..i])) { LocalFallback = line.LocalFallback }, speakers[first]);
                    first = i;
                }
                yield break;
            }
        }
        yield return (line, Speaker(line.Start, line.End, segments, true));
    }

    public static IReadOnlyList<SpeakerSegment>? Segments(ReadOnlyMemory<byte> reply)
    {
        try
        {
            using var json = JsonDocument.Parse(reply);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("segments", out var list) || list.ValueKind != JsonValueKind.Array ||
                list.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object)) return null;
            var result = new List<SpeakerSegment>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.TryGetProperty("speaker", out var id) && id.ValueKind == JsonValueKind.String &&
                    item.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Number &&
                    item.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.Number &&
                    start.TryGetDouble(out var from) && end.TryGetDouble(out var to) && double.IsFinite(from) && double.IsFinite(to) && to > from)
                    result.Add(new(id.GetString()!, from, to));
            }
            return result;
        }
        catch (JsonException) { return null; }
    }
}

public sealed partial record Meeting
{
    public void RenameSpeaker(int number, string name, DateTime? now = null)
    {
        if (Speakers is null) return;
        var old = MeetingSpeakers.Label(number, this);
        name = string.Concat(SwiftText.Characters(SwiftText.TrimWhitespaceAndNewlines(name)).Take(60));
        if (name.Length == 0) Speakers.Names.Remove(number);
        else Speakers.Names[number] = name;
        Speakers = Speakers with { EditedAt = now ?? DateTime.UtcNow };
        MarkNotesOutdated(false);
        RelabelNotes(old, number);
    }

    public void MergeSpeaker(int from, int into, DateTime? now = null)
    {
        if (Speakers is null || from == into) return;
        var old = MeetingSpeakers.Label(from, this);
        foreach (var line in Lines.Where(line => line.Speaker == from)) line.Speaker = into;
        Speakers.Names.Remove(from);
        Speakers = Speakers with { EditedAt = now ?? DateTime.UtcNow };
        MarkNotesOutdated(false);
        RelabelNotes(old, into);
    }

    public void ReassignLine(int id, int speaker, DateTime? now = null)
    {
        if (Speakers is null || speaker <= 0 || Lines.FirstOrDefault(line => line.Id == id && line.Stream == Source.SpeakerStream) is not { } target) return;
        target.Speaker = speaker;
        Speakers = Speakers with { EditedAt = now ?? DateTime.UtcNow };
        MarkNotesOutdated();
    }

    void RelabelNotes(string old, int number)
    {
        string label = MeetingSpeakers.Label(number, this);
        if (Notes is not { } notes || old == label) return;
        if (MeetingSpeakers.Numbers(this).Where(other => other != number).Select(other => MeetingSpeakers.Label(other, this))
            .Concat(["Me", "Them", "Room"]).Contains(label)) { MarkNotesOutdated(); return; }
        string? Swap(string? text) => text is null ? null : MeetingSpeakers.Replace(old, label, text);
        NoteItem[] Items(IEnumerable<NoteItem> items) => items.Select(item => item with { Text = Swap(item.Text)!, Owner = Swap(item.Owner) }).ToArray();
        Notes = notes with { Title = Swap(notes.Title), Summary = Swap(notes.Summary),
            KeyPoints = Items(notes.KeyPoints), Decisions = Items(notes.Decisions), Actions = Items(notes.Actions) };
    }
}
