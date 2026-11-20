// Port of app/MeetingExport.swift.
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Murmur.Core;

public static class MeetingExport
{
    internal static readonly char[] Newlines = ['\n', '\r', '\v', '\f', '\u0085', '\u2028', '\u2029'];

    public static string Clock(double seconds)
    {
        long total = double.IsFinite(seconds) && seconds >= 0 && seconds < long.MaxValue ? (long)seconds : 0;
        return total >= 3600 ? FormattableString.Invariant($"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}")
            : FormattableString.Invariant($"{total / 60}:{total % 60:00}");
    }

    public static string Transcript(Meeting meeting) => string.Join('\n', meeting.Lines.Where(line => !line.IsHidden)
        .Select(line => $"[{Clock(line.Start)}] {MeetingSpeakers.Label(line, meeting)}: {line.VisibleText}"));

    public static string Markdown(Meeting meeting)
    {
        var output = new StringBuilder($"# {Escape(meeting.Title)}\n\n");
        output.Append($"- Date: {SwiftDate.Normalize(meeting.RecordedAt).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}\n- Length: {Clock(meeting.Seconds)}\n");
        output.Append($"- Audio: {(meeting.Source.MicOnly ? "my microphone only" : $"my microphone and {Escape(meeting.Source.Name)}")}\n");
        output.Append($"- Transcribed: {(meeting.TranscribedBy is { } provider ? $"by {Escape(provider)}" : "on this PC")}\n");
        if (meeting.Speakers?.State == SpeakerState.Done && MeetingSpeakers.Numbers(meeting) is { Length: > 0 } numbers)
            output.Append($"- Speakers: {Escape(string.Join(", ", numbers.Select(number => MeetingSpeakers.Label(number, meeting)).Prepend("Me")))}\n");
        if (meeting.Notes is { IsEmpty: false }) output.Append($"\n## Notes\n\n{Notes(meeting, markdown: true)}\n");
        if (meeting.LectureNotes is not null) output.Append($"\n## Lecture notes\n\n{Notes(meeting, markdown: true, lecture: true)}\n");
        var mine = SwiftText.TrimWhitespaceAndNewlines(meeting.MyNotes);
        if (mine.Length > 0) output.Append($"\n## My notes\n\n{mine}\n");
        output.Append("\n## Transcript\n\n");
        foreach (var line in meeting.Lines.Where(line => !line.IsHidden))
            output.Append($"**{Escape(MeetingSpeakers.Label(line, meeting))}** [{Clock(line.Start)}]: {Escape(line.VisibleText)}\n\n");
        return output.ToString();
    }

    public static string Notes(Meeting meeting, bool markdown = false, bool lecture = false)
    {
        if ((lecture ? meeting.LectureNotes : meeting.Notes) is not { } notes) return "";
        var times = new Dictionary<int, double>();
        foreach (var line in meeting.Lines) times.TryAdd(line.Id, line.Start);
        string Text(string value) => markdown ? Escape(value) : value;
        string Item(NoteItem item, bool box)
        {
            string line = (box ? item.Done == true ? "- [x] " : "- [ ] " : "- ") + Text(item.Text);
            var extra = new List<string>();
            if (item.Owner is { } owner) extra.Add(Text(owner));
            if (item.Due is { } due) extra.Add("due " + Text(due));
            if (extra.Count > 0) line += " (" + string.Join(", ", extra) + ")";
            var first = item.Lines.Where(times.ContainsKey).Select(id => (double?)times[id]).Min();
            return first is { } time ? line + $" [{Clock(time)}]" : line;
        }
        var sections = new List<string>();
        if (!markdown) sections.Add(meeting.Title);
        if (notes.Summary is { } summary) sections.Add(Text(summary));
        var titles = NoteSections.Titles(lecture);
        string heading = markdown ? "### " : "";
        void Add(string title, IReadOnlyList<NoteItem> items, bool box = false)
        {
            if (items.Count > 0) sections.Add(heading + title + "\n" + string.Join('\n', items.Select(item => Item(item, box))));
        }
        Add(titles.Points, notes.KeyPoints);
        Add(titles.Decisions, notes.Decisions);
        Add(titles.Actions, notes.Actions, !lecture);
        sections.Add($"Written by {Text(notes.Route)} from the transcript{(notes.Outdated == true ? "; out of date" : "")}.");
        return string.Join("\n\n", sections);
    }

    public static string Escape(string text)
    {
        var output = new StringBuilder(text.Length);
        if (text.Length > 0 && "#>-+|".Contains(text[0]) && StringInfo.GetNextTextElementLength(text) == 1) output.Append('\\');
        foreach (char c in text)
        {
            if ("\\*_<[]()!&`".Contains(c)) output.Append('\\');
            output.Append(Newlines.Contains(c) ? ' ' : c);
        }
        return output.ToString();
    }

    public static string FileName(Meeting meeting) =>
        "Meeting " + meeting.RecordedAt.ToLocalTime().ToString("yyyy-MM-dd HH.mm", CultureInfo.InvariantCulture) + ".md";

    public static string Json(Meeting meeting) => JsonSerializer.Serialize(meeting, MeetingStore.Json);
}
