// Port of the writer in app/MeetingNotes.swift.
namespace Murmur.Core;

public sealed class NotesFailure(string message) : Exception(message);

public static class MeetingNotesWriter
{
    /// <summary>Transcript characters per extraction request and drafts per merge batch for a cloud route; the on-device route passes <see cref="MeetingNotesLimits.LocalChunkCharacters"/>.</summary>
    public const int ChunkCharacters = 24_000;
    public sealed record Outcome(MeetingNotes Notes, IReadOnlyDictionary<NotesGrounding.Reason, int> Reasons);

    public static Task<Outcome> WriteAsync(Meeting meeting, MeetingNotesRoute route, ICleanupProvider? custom,
        bool lecture = false, DateTime? now = null, CancellationToken cancellationToken = default)
    {
        if (route == MeetingNotesRoute.None)
            return WriteAsync(meeting, route, route.Label(), (_, _) => throw new InvalidOperationException(),
                lecture, now, cancellationToken: cancellationToken);
        if (custom is null || custom.Id != ProviderID.CustomAPI)
            throw new NotesFailure("Set up a Custom Provider to write meeting notes.");
        var provider = custom.ForMeetingNotes();
        return WriteAsync(meeting, route, route.Label(), provider.CleanupAsync, lecture, now, cancellationToken: cancellationToken);
    }

    public static async Task<Outcome> WriteAsync(Meeting meeting, MeetingNotesRoute route, string routeName,
        Func<CleanupMessages, CancellationToken, Task<string>> generate, bool lecture = false, DateTime? now = null,
        Func<string>? nonce = null, CancellationToken cancellationToken = default, int chunkCharacters = ChunkCharacters)
    {
        var notes = new MeetingNotes(routeName, now ?? DateTime.UtcNow);
        var reasons = new Dictionary<NotesGrounding.Reason, int>();
        if (route == MeetingNotesRoute.None) return new(notes, reasons);
        var numbered = MeetingNotesPrompt.Number(meeting);
        if (numbered.Length == 0) return new(notes, reasons);

        nonce ??= PromptTemplate.NewNonce;
        // One request. A context overflow (PayloadTooLarge) or a reply that stopped at the output cap (OutputTruncated) splits the batch in half, once,
        // and each half is a request of its own; a half that fails the same way fails the notes. A lone draft needs no merge. Every reply is cut to the item caps.
        async Task<Draft[]> Request<T>(T[] batch, Func<T[], string, CleanupMessages> build, int splitAbove, string tooLong, bool splitOnOverflow)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (batch is Draft[] { Length: 1 } lone) return lone;
            try
            {
                var prompt = build(batch, nonce());
                if (lecture) prompt = prompt with { System = prompt.System + "\n" + MeetingNotesPrompt.LectureRules };
                var reply = await generate(prompt, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return [(Draft.Parse(reply) ?? throw new NotesFailure("The notes route did not answer with notes.")).Capped()];
            }
            catch (ProviderException ex) when (ex.Error.Case is ProviderErrorKind.PayloadTooLarge or ProviderErrorKind.OutputTruncated)
            {
                if (!splitOnOverflow || batch.Length <= splitAbove) throw new NotesFailure(tooLong);
                int half = batch.Length / 2;
                var first = await Request(batch[..half], build, splitAbove, tooLong, false).ConfigureAwait(false);
                var second = await Request(batch[half..], build, splitAbove, tooLong, false).ConfigureAwait(false);
                return [.. first, .. second];
            }
        }

        var whole = NotesGrounding.From(numbered, meeting.MyNotes);
        var level = new List<Draft>();
        foreach (var chunk in Chunks(numbered, chunkCharacters))
        {
            var parts = await Request(chunk, (lines, id) => MeetingNotesPrompt.Extract(lines, meeting.MyNotes, id),
                1, "Part of this meeting is too long for the notes route.", true).ConfigureAwait(false);
            level.AddRange(parts.Select(part => Ground(part, whole, reasons, lecture).Capped()));
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
                if (batch.Count >= 2 && size + length > chunkCharacters)
                {
                    batches.Add(batch.ToArray());
                    batch.Clear();
                    size = 0;
                }
                batch.Add(part);
                size += length;
            }
            if (batch.Count == 1 && batches.Count > 0) batches[^1] = [.. batches[^1], batch[0]];
            else if (batch.Count > 0) batches.Add(batch.ToArray());

            var next = new List<Draft>();
            foreach (var items in batches)
                next.AddRange(await Request(items, (parts, id) => MeetingNotesPrompt.Merge(parts, id, speakers),
                    2, "This meeting is too long for the notes route.", true).ConfigureAwait(false));
            if (next.Count >= level.Count) throw new NotesFailure("This meeting is too long for the notes route.");
            level = next;
        }

        var draft = Ground(level.Single(), whole, reasons, lecture).Capped();
        var ids = numbered.ToDictionary(line => line.Number, line => line.Line.Id);
        NoteItem[] Items(IEnumerable<Draft.Item> items, bool action = false) =>
            items.Select(item => new NoteItem(item.Text, item.Lines.Select(id => ids[id]).ToArray(), item.Owner, item.Due, action ? false : null)).ToArray();
        notes = notes with
        {
            Title = draft.Title is { } title && MeetingEcho.Split(title).Length <= 8 && NotesGrounding.FreeText(title, whole, title: true) ? title : null,
            Summary = NotesGrounding.FreeText(draft.Summary, whole) ? draft.Summary : null,
            KeyPoints = Items(draft.Points), Decisions = Items(draft.Decisions), Actions = Items(draft.Actions, !lecture),
            Dropped = reasons.Values.Sum(),
        };
        return new(notes, reasons);
    }

    public static IEnumerable<MeetingNotesPrompt.Numbered[]> Chunks(IEnumerable<MeetingNotesPrompt.Numbered> lines, int budget)
    {
        var current = new List<MeetingNotesPrompt.Numbered>();
        int size = 0;
        foreach (var line in lines)
        {
            int length = SwiftText.Count(MeetingNotesPrompt.Render(line)) + 1;
            if (current.Count > 0 && size + length > budget)
            {
                yield return current.ToArray();
                current.Clear();
                size = 0;
            }
            current.Add(line);
            size += length;
        }
        if (current.Count > 0) yield return current.ToArray();
    }

    public static Draft Ground(Draft draft, NotesGrounding.Source source, IDictionary<NotesGrounding.Reason, int> reasons, bool lecture = false)
    {
        Draft.Item[] Keep(IEnumerable<Draft.Item> input, NotesGrounding.Kind kind)
        {
            var kept = new List<Draft.Item>();
            foreach (var original in input)
            {
                var item = original with { Lines = original.Lines.Distinct().ToArray() };
                if (NotesGrounding.Check(item, kind, source) is { } reason)
                    reasons[reason] = reasons.TryGetValue(reason, out int count) ? count + 1 : 1;
                else kept.Add(item);
            }
            return kept.ToArray();
        }

        return draft with
        {
            Points = Keep(draft.Points, NotesGrounding.Kind.Point),
            Decisions = Keep(draft.Decisions, lecture ? NotesGrounding.Kind.Point : NotesGrounding.Kind.Decision),
            Actions = Keep(draft.Actions, lecture ? NotesGrounding.Kind.Point : NotesGrounding.Kind.Action),
        };
    }
}
