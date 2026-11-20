// Port of app/MeetingNotes.swift.
using System.Globalization;
using System.Text.Json.Serialization;

namespace Murmur.Core;

public sealed record NoteItem(string Text, IReadOnlyList<int> Lines, string? Owner = null, string? Due = null, bool? Done = null);

public sealed record MeetingNotes(string Route, [property: JsonConverter(typeof(SwiftDateConverter))] DateTime WrittenAt)
{
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<NoteItem> KeyPoints { get; init; } = [];
    public IReadOnlyList<NoteItem> Decisions { get; init; } = [];
    public IReadOnlyList<NoteItem> Actions { get; init; } = [];
    public bool? Outdated { get; init; }
    public int Dropped { get; init; }
    [JsonIgnore] public bool IsEmpty => Summary is null && KeyPoints.Count == 0 && Decisions.Count == 0 && Actions.Count == 0;
}

public static class NoteSections
{
    public static (string Points, string Decisions, string Actions) Titles(bool lecture) =>
        lecture ? ("Concepts", "Examples", "Perspectives") : ("Key points", "Decisions", "Action items");
}

public enum MeetingNotesRoute { None, CustomProvider }

public static class MeetingNotesRoutes
{
    public static MeetingNotesRoute? Load(ISettingsStore settings) => (settings.Get("meetingNotesRoute") as string) switch
    {
        "none" => MeetingNotesRoute.None,
        "custom-api" => MeetingNotesRoute.CustomProvider,
        _ => null,
    };

    public static string Label(this MeetingNotesRoute route) => route == MeetingNotesRoute.None ? "None (transcript only)" : "Custom Provider";

    public static string Disclosure(this MeetingNotesRoute route, string? host, bool local, string? retention)
    {
        if (route == MeetingNotesRoute.None) return "No notes are written. The meeting keeps its transcript. Nothing leaves this PC.";
        host ??= "the provider";
        return local ? $"Notes are written by the model server on this PC ({host}). Nothing leaves this PC."
            : $"Writing notes sends the meeting transcript to {host}. It contains other people’s words, speaker names and your own notes. {retention}";
    }
}

internal sealed record MeetingNotesData(Dictionary<string, string> Prompt, HashSet<string> Stopwords, string[] Addressed,
    HashSet<string> ClauseBreaks, HashSet<string>[][] Opposites, HashSet<string> Months, HashSet<string> DateWords,
    HashSet<string> NotNames, HashSet<string> SubjectCues, Dictionary<string, int> Units, Dictionary<string, int> Scales,
    string[] Agreements, string[] Commitments)
{
    public static readonly MeetingNotesData Shared = SharedData.Load<MeetingNotesData>("meeting_notes");
}

public static class MeetingNotesPrompt
{
    public sealed record Numbered(int Number, MeetingLine Line, string Speaker);

    public static Numbered[] Number(Meeting meeting) => meeting.Lines.Where(line => !line.IsHidden)
        .Select((line, index) => new Numbered(index + 1, line, MeetingSpeakers.Label(line, meeting))).ToArray();

    public static string[] Speakers(IEnumerable<Numbered> lines)
    {
        var labels = lines.Where(line => line.Line.Stream == MeetingStream.Them || line.Line.Speaker is not null)
            .Select(line => line.Speaker).Distinct().ToArray();
        return labels is ["Them"] ? [] : labels;
    }

    public static string Render(Numbered line) =>
        FormattableString.Invariant($"[L{line.Number} {line.Speaker} {MeetingExport.Clock(line.Line.Start)}] {line.Line.VisibleText}");

    static string Nonce(string nonce) => PromptTemplate.Id(nonce);

    public static CleanupMessages Extract(IReadOnlyList<Numbered> lines, string myNotes, string nonce)
    {
        var id = Nonce(nonce);
        var tag = "lines-" + id;
        var notesTag = "my-notes-" + id;
        var speakers = Speakers(lines);
        var room = lines.Any(line => line.Speaker == "Room" || line.Line.Stream == MeetingStream.Me && line.Line.Speaker is not null);
        string template = "extract" + (room ? "Room" : "") + (speakers.Length > 0 ? "Speakers" : "");
        var system = SystemMessage(template, id, speakers);
        var user = $"<{tag}>\n{string.Join('\n', lines.Select(Render))}\n</{tag}>";
        var notes = SwiftText.TrimWhitespaceAndNewlines(myNotes);
        if (notes.Length > 0) user += $"\n<{notesTag}>\n{notes}\n</{notesTag}>";
        return new(system, user);
    }

    public static CleanupMessages Merge(IEnumerable<Draft> parts, string nonce, string[] speakers)
    {
        var id = Nonce(nonce);
        var tag = "parts-" + id;
        return new(SystemMessage(speakers.Length == 0 ? "merge" : "mergeSpeakers", id, speakers),
            $"<{tag}>\n{string.Join("\n\n", parts.Select(part => part.Text))}\n</{tag}>");
    }

    static string SystemMessage(string template, string nonce, string[] speakers) =>
        PromptTemplate.Fill(MeetingNotesData.Shared.Prompt[template], ("nonce", nonce), ("speakers", string.Join(", ", speakers)));

    public static string LectureRules => MeetingNotesData.Shared.Prompt["lectureRules"];
}

public sealed record Draft
{
    public sealed record Item(string Text, IReadOnlyList<int> Lines, string? Owner = null, string? Due = null);
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<Item> Points { get; init; } = [];
    public IReadOnlyList<Item> Decisions { get; init; } = [];
    public IReadOnlyList<Item> Actions { get; init; } = [];

    public string Text
    {
        get
        {
            static string Cites(Item item) => string.Join(' ', item.Lines.Select(id => FormattableString.Invariant($"L{id}")));
            var lines = new List<string>();
            if (Title is not null) lines.Add("TITLE | " + Title);
            if (Summary is not null) lines.Add("SUMMARY | " + Summary);
            lines.AddRange(Decisions.Select(item => $"DECISION | {Cites(item)} | {item.Text}"));
            lines.AddRange(Actions.Select(item => $"ACTION | {Cites(item)} | {item.Owner ?? "-"} | {item.Due ?? "-"} | {item.Text}"));
            lines.AddRange(Points.Select(item => $"POINT | {Cites(item)} | {item.Text}"));
            return string.Join('\n', lines);
        }
    }

    public static Draft? Parse(string reply)
    {
        string? title = null, summary = null;
        var points = new List<Item>();
        var decisions = new List<Item>();
        var actions = new List<Item>();
        bool found = false;
        static string? Value(string text)
        {
            var value = Whitespace(text);
            return value.Length == 0 || value == "-" || value.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : value;
        }

        foreach (var raw in reply.Split(MeetingExport.Newlines))
        {
            var fields = raw.Trim(' ', '\t', '-', '*', '•', '>', '`', '#').Split('|')
                .Select(text => Whitespace(text).Trim('*')).ToArray();
            if (fields.Length < 2) continue;
            string kind = fields[0].ToUpperInvariant().Trim(':', ' ');
            switch (kind)
            {
                case "TITLE": title = Value(string.Join(' ', fields[1..])); break;
                case "SUMMARY": summary = Value(string.Join(" | ", fields[1..])); break;
                case "POINT" or "KEY POINT" or "DECISION" when fields.Length >= 3 && Value(string.Join(" | ", fields[2..])) is { } text:
                    (kind == "DECISION" ? decisions : points).Add(new(Cleaned(text), Citations(fields[1])));
                    break;
                case "ACTION" or "ACTION ITEM" when fields.Length >= 5 && Value(string.Join(" | ", fields[4..])) is { } text:
                    actions.Add(new(Cleaned(text), Citations(fields[1]), Value(fields[2]), Value(fields[3])));
                    break;
                case "ACTION" or "ACTION ITEM" when fields.Length == 3:
                    var parts = fields[2].Split("->", StringSplitOptions.None);
                    if (Value(parts[^1]) is not { } action) continue;
                    actions.Add(new(Cleaned(action), Citations(fields[1]),
                        parts.Length > 1 ? Value(parts[0]) : null, parts.Length > 2 ? Value(parts[1]) : null));
                    break;
                default: continue;
            }
            found = true;
        }
        return found ? new() { Title = title, Summary = summary, Points = points, Decisions = decisions, Actions = actions } : null;
    }

    static string Whitespace(string text)
    {
        int start = 0, end = text.Length;
        static bool Space(char c) => char.IsWhiteSpace(c) && !MeetingExport.Newlines.Contains(c);
        while (start < end && Space(text[start])) start++;
        while (end > start && Space(text[end - 1])) end--;
        return text[start..end];
    }

    public static string Cleaned(string text)
    {
        foreach (var label in new[] { "Owner:", "Due:", "Owner -", "Due -" })
        {
            int start = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            int end = text.IndexOfAny(['.', ';', '|'], start + label.Length);
            text = text.Remove(start, (end < 0 ? text.Length : end + 1) - start);
        }
        return Whitespace(text);
    }

    public static int[] Citations(string field)
    {
        var result = new List<int>();
        foreach (var group in field.Split([' ', ',', ';', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            var bounds = new List<int>();
            foreach (var piece in group.Split(['-', '–'], StringSplitOptions.RemoveEmptyEntries))
            {
                var digits = string.Concat(SwiftText.Characters(piece).Where(SwiftText.IsNumber));
                if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) bounds.Add(value);
            }
            if (bounds.Count == 2 && bounds[0] <= bounds[1] && bounds[1] - bounds[0] < 50)
                result.AddRange(Enumerable.Range(bounds[0], bounds[1] - bounds[0] + 1));
            else if (bounds.Count == 1) result.Add(bounds[0]);
        }
        return result.ToArray();
    }
}
