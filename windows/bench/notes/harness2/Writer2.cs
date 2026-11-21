// Bench prototype of MeetingNotesWriter with (a) item caps on parsed extraction and merge outputs,
// (b) one split-and-retry when a reply exhausts the output cap, (c) a merge fan-out limit.
using Murmur.Core;

public sealed class OutputExhausted(string stage) : Exception("output cap: " + stage) { public string Stage { get; } = stage; }

public sealed record Opts(bool Cap, bool Split, int Fan, int Budget, int MaxPoints = 8, int MaxDecisions = 10, int MaxActions = 12);

public static class Writer2
{
    public static int Truncated, Splits, SplitRecovered, CapHits;

    public static Draft CapItems(Draft d, Opts o)
    {
        if (!o.Cap) return d;
        int before = d.Points.Count + d.Decisions.Count + d.Actions.Count;
        var r = d with { Points = d.Points.Take(o.MaxPoints).ToArray(), Decisions = d.Decisions.Take(o.MaxDecisions).ToArray(), Actions = d.Actions.Take(o.MaxActions).ToArray() };
        Truncated += before - (r.Points.Count + r.Decisions.Count + r.Actions.Count);
        return r;
    }

    public static async Task<MeetingWriter.Result> Write(Meeting meeting, Opts o, Func<CleanupMessages, CancellationToken, Task<string>> generate, CancellationToken ct = default)
    {
        var notes = new MeetingNotes("llama", DateTime.UtcNow);
        var reasons = new Dictionary<NotesGrounding.Reason, int>();
        var numbered = MeetingNotesPrompt.Number(meeting);
        if (numbered.Length == 0) return new(notes, reasons);
        var whole = NotesGrounding.From(numbered, meeting.MyNotes);

        async Task<Draft[]> Request<T>(T[] batch, Func<T[], string, CleanupMessages> build, int splitAbove, string stage, bool canSplit, bool splitOverflow)
        {
            if (batch is Draft[] single && single.Length == 1) return single;
            try
            {
                var prompt = build(batch, Guid.NewGuid().ToString("N"));
                var reply = await generate(prompt, ct);
                var d = Draft.Parse(reply) ?? throw new NotesFailure("The notes route did not answer with notes.");
                return [CapItems(d, o)];
            }
            catch (Exception ex) when ((ex is ProviderException pe && pe.Error.Case == ProviderErrorKind.PayloadTooLarge && splitOverflow) || (ex is OutputExhausted && o.Split))
            {
                if (ex is OutputExhausted) CapHits++;
                if (!canSplit || batch.Length <= splitAbove) throw new NotesFailure("too long: " + stage);
                Splits++;
                int half = batch.Length / 2;
                var first = await Request(batch[..half], build, splitAbove, stage, false, splitOverflow);
                var second = await Request(batch[half..], build, splitAbove, stage, false, splitOverflow);
                SplitRecovered++;
                return [.. first, .. second];
            }
        }

        var level = new List<Draft>();
        foreach (var chunk in MeetingNotesWriter.Chunks(numbered, o.Budget))
        {
            var parts = await Request(chunk, (lines, id) => MeetingNotesPrompt.Extract(lines, meeting.MyNotes, id), 1, "extract", true, true);
            level.AddRange(parts.Select(p => CapItems(MeetingNotesWriter.Ground(p, whole, reasons), o)));
        }

        var speakers = MeetingNotesPrompt.Speakers(numbered);
        while (level.Count > 1)
        {
            var batches = new List<Draft[]>();
            var batch = new List<Draft>();
            int size = 0;
            foreach (var part in level)
            {
                int length = SwiftText.Count(part.Text) + 2;
                if (batch.Count >= 2 && (size + length > o.Budget || (o.Fan > 0 && batch.Count >= o.Fan)))
                {
                    batches.Add(batch.ToArray()); batch.Clear(); size = 0;
                }
                batch.Add(part); size += length;
            }
            if (batch.Count == 1 && batches.Count > 0) batches[^1] = [.. batches[^1], batch[0]];
            else if (batch.Count > 0) batches.Add(batch.ToArray());
            var next = new List<Draft>();
            foreach (var items in batches)
                next.AddRange((await Request(items, (parts, id) => MeetingNotesPrompt.Merge(parts, id, speakers), 2, "merge", true, true)).Select(p => p));
            if (next.Count >= level.Count) throw new NotesFailure("too long: merge no progress");
            level = next;
        }

        var draft = CapItems(MeetingNotesWriter.Ground(level.Single(), whole, reasons), o);
        var ids = numbered.ToDictionary(l => l.Number, l => l.Line.Id);
        NoteItem[] Items(IEnumerable<Draft.Item> items, bool action = false) =>
            items.Select(i => new NoteItem(i.Text, i.Lines.Select(id => ids[id]).ToArray(), i.Owner, i.Due, action ? false : null)).ToArray();
        notes = notes with
        {
            Title = draft.Title is { } t && t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8 && NotesGrounding.FreeText(t, whole, title: true) ? t : null,
            Summary = NotesGrounding.FreeText(draft.Summary, whole) ? draft.Summary : null,
            KeyPoints = Items(draft.Points), Decisions = Items(draft.Decisions), Actions = Items(draft.Actions, true),
            Dropped = reasons.Values.Sum(),
        };
        return new(notes, reasons);
    }
}

public static class MeetingWriter { public sealed record Result(MeetingNotes Notes, IReadOnlyDictionary<NotesGrounding.Reason, int> Reasons); }
