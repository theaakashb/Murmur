using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>The notes contract change shared with the Mac (windows/notes-contract-MAC.md): item caps in the prompts and after parsing,
/// one split-and-retry when a reply ends at the output cap, and a long meeting that completes. The ports of the check_meetings cases the Mac PR adds.</summary>
public sealed class MeetingNotesContractTests
{
    const string Words = "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november oscar papa quebec romeo sierra tango";
    static readonly string[] Vocabulary = Words.Split(' ');

    static Meeting Spoken(int lines)
    {
        var meeting = MeetingTests.NewMeeting();
        for (int i = 0; i < lines; i++) meeting.Insert(MeetingStream.Them, i * 20, i * 20 + 10, $"We reviewed {Vocabulary[i % Vocabulary.Length]} and {Vocabulary[(i + 1) % Vocabulary.Length]} for the plan.");
        return meeting;
    }

    static Task<MeetingNotesWriter.Outcome> Write(Meeting meeting, Func<CleanupMessages, Task<string>> generate, int chunkCharacters = MeetingNotesWriter.ChunkCharacters, bool lecture = false) =>
        MeetingNotesWriter.WriteAsync(meeting, MeetingNotesRoute.CustomProvider, "Synthetic", (prompt, _) => generate(prompt), lecture, nonce: () => "a-b12!", chunkCharacters: chunkCharacters);

    static ProviderException Exhausted() => new(ProviderError.OutputTruncated(ProviderID.CustomAPI));
    static bool IsMerge(CleanupMessages prompt) => prompt.System.StartsWith("You merge", StringComparison.Ordinal);

    [Fact]
    public void CapConstantsAndInstruction()
    {
        Assert.Equal((8, 10, 12, 1200), (MeetingNotesLimits.MaxPoints, MeetingNotesLimits.MaxDecisions, MeetingNotesLimits.MaxActions, MeetingNotesLimits.ReplyTokens));
        Assert.Equal("Return at most 8 POINT, 10 DECISION and 12 ACTION lines. Prefer the most important supported items.", MeetingNotesPrompt.CapInstruction);
    }

    [Fact]
    public void EveryExtractionAndMergePromptEndsWithTheCapLine()
    {
        var meeting = Spoken(2);
        var numbered = MeetingNotesPrompt.Number(meeting);
        var draft = new Draft { Points = [new("Alpha plan", [1])] };
        foreach (var system in new[]
        {
            MeetingNotesPrompt.Extract(numbered, "", "n").System,
            MeetingNotesPrompt.Extract([numbered[0] with { Speaker = "Room" }], "", "n").System,
            MeetingNotesPrompt.Extract([numbered[0] with { Speaker = "Dana" }], "", "n").System,
            MeetingNotesPrompt.Merge([draft, draft], "n", []).System,
            MeetingNotesPrompt.Merge([draft, draft], "n", ["Dana"]).System,
        })
        {
            Assert.EndsWith("- Leave a list empty rather than inventing items.\n" + MeetingNotesPrompt.CapInstruction, system);
            Assert.Equal(1, system.Split(MeetingNotesPrompt.CapInstruction).Length - 1);
        }
    }

    [Fact]
    public async Task LectureRulesFollowTheCapLine()
    {
        string? system = null;
        await Write(Spoken(1), prompt => { system = prompt.System; return Task.FromResult("POINT | L1 | Alpha plan"); }, lecture: true);
        Assert.EndsWith(MeetingNotesPrompt.CapInstruction + "\n" + MeetingNotesPrompt.LectureRules, system);
    }

    [Fact]
    public void CappedKeepsTheFirstItemsOfEachKind()
    {
        var items = Enumerable.Range(1, 20).Select(i => new Draft.Item("Item " + i, [i])).ToArray();
        var draft = new Draft { Title = "T", Summary = "S", Points = items, Decisions = items, Actions = items }.Capped();
        Assert.Equal((8, 10, 12), (draft.Points.Count, draft.Decisions.Count, draft.Actions.Count));
        Assert.Equal("Item 8", draft.Points[^1].Text);
        Assert.Equal("Item 12", draft.Actions[^1].Text);
        Assert.Equal(("T", "S"), (draft.Title, draft.Summary));
        Assert.Equal(3, new Draft { Points = items[..3] }.Capped().Points.Count);
    }

    [Fact]
    public async Task ExtractionRepliesAreCutToTheCaps()
    {
        var meeting = Spoken(1);
        var reply = string.Join('\n', Enumerable.Range(0, 12).Select(i => "POINT | L1 | Alpha plan " + (char)('a' + i)).Concat(
            Enumerable.Range(0, 14).Select(i => "DECISION | L1 | Agreed alpha plan " + (char)('a' + i))).Concat(
            Enumerable.Range(0, 15).Select(i => "ACTION | L1 | Me | - | Review alpha plan " + (char)('a' + i))));
        var outcome = await Write(meeting, _ => Task.FromResult(reply));
        Assert.True(outcome.Notes.KeyPoints.Count <= 8);
        Assert.True(outcome.Notes.Decisions.Count <= 10);
        Assert.True(outcome.Notes.Actions.Count <= 12);
        Assert.NotEmpty(outcome.Notes.KeyPoints);
    }

    [Fact]
    public async Task MergeRepliesAreCutToTheCaps()
    {
        var meeting = Spoken(6);
        int merges = 0;
        var outcome = await Write(meeting, prompt =>
        {
            if (!IsMerge(prompt)) return Task.FromResult("TITLE | Plan review\nPOINT | L1 | Alpha plan");
            merges++;
            return Task.FromResult("TITLE | Plan review\n" + string.Join('\n', Enumerable.Range(0, 20).Select(i => "POINT | L1 | Alpha plan " + (char)('a' + i))));
        }, chunkCharacters: 80);
        Assert.True(merges >= 1);
        Assert.True(outcome.Notes.KeyPoints.Count <= MeetingNotesLimits.MaxPoints);
        Assert.Equal("Plan review", outcome.Notes.Title);
    }

    [Fact]
    public async Task ExhaustionSplitsTheChunkOnceAndBothHalvesAreAsked()
    {
        var meeting = Spoken(4);
        var seen = new List<string>();
        int extracts = 0, merges = 0;
        var outcome = await Write(meeting, prompt =>
        {
            if (IsMerge(prompt)) { merges++; return Task.FromResult("TITLE | Plan review\nPOINT | L1 L3 | Alpha plan"); }
            seen.Add(prompt.User);
            if (++extracts == 1) throw Exhausted();
            return Task.FromResult(prompt.User.Contains("[L1", StringComparison.Ordinal) ? "POINT | L1 | Alpha plan" : "POINT | L3 | Charlie plan");
        });
        Assert.Equal(3, extracts);
        Assert.Equal(1, merges);
        Assert.Contains("[L4", seen[0]);
        Assert.DoesNotContain("[L3", seen[1]);
        Assert.DoesNotContain("[L2", seen[2]);
        Assert.Contains("[L3", seen[2]);
        Assert.Equal("Plan review", outcome.Notes.Title);
    }

    [Fact]
    public async Task ExhaustionRetriesOnceThenFails()
    {
        int calls = 0;
        var failed = await Assert.ThrowsAsync<NotesFailure>(() => Write(Spoken(4), _ => { calls++; throw Exhausted(); }));
        Assert.Equal("Part of this meeting is too long for the notes route.", failed.Message);
        Assert.Equal(2, calls);   // the whole chunk, then its first half; the half is never split again

        calls = 0;
        var single = await Assert.ThrowsAsync<NotesFailure>(() => Write(Spoken(1), _ => { calls++; throw Exhausted(); }));
        Assert.Equal("Part of this meeting is too long for the notes route.", single.Message);
        Assert.Equal(1, calls);   // one line cannot be split
    }

    [Fact]
    public async Task MergeExhaustionSplitsABatchAndALoneDraftPassesThrough()
    {
        var meeting = Spoken(6);
        var mergeInputs = new List<int>();
        var outcome = await Write(meeting, prompt =>
        {
            if (!IsMerge(prompt)) return Task.FromResult("POINT | L1 | Alpha plan");
            int parts = prompt.User.Split("POINT |").Length - 1;
            mergeInputs.Add(parts);
            if (parts == 3) throw Exhausted();
            return Task.FromResult("TITLE | Plan review\nPOINT | L1 | Alpha plan");
        }, chunkCharacters: 90);
        Assert.Contains(3, mergeInputs);
        Assert.NotNull(outcome.Notes.Title);
        Assert.DoesNotContain(1, mergeInputs);   // a lone draft is never sent to the merge
    }

    [Fact]
    public async Task MergeExhaustionRetriesOnceThenFails()
    {
        var failed = await Assert.ThrowsAsync<NotesFailure>(() => Write(Spoken(8), prompt =>
            IsMerge(prompt) ? throw Exhausted() : Task.FromResult("POINT | L1 | Alpha plan"), chunkCharacters: 90));
        Assert.Equal("This meeting is too long for the notes route.", failed.Message);
    }

    [Fact]
    public async Task GroundingStillDropsUnsupportedItemsInsideTheCaps()
    {
        var outcome = await Write(Spoken(1), _ => Task.FromResult("TITLE | Plan\nPOINT | L1 | Alpha plan\nPOINT | L1 | Zulu launches in 2031\nDECISION | L9 | Bravo plan"));
        Assert.Equal("Alpha plan", Assert.Single(outcome.Notes.KeyPoints).Text);
        Assert.Empty(outcome.Notes.Decisions);
        Assert.Equal(2, outcome.Notes.Dropped);
        Assert.Equal(1, outcome.Reasons[NotesGrounding.Reason.UnknownLine]);
    }

    [Fact]
    public async Task ASixtyMinuteMeetingCompletesOnTheLocalBudgetWithAFakeEngine()
    {
        // About 60 minutes: 13 extraction chunks of 5,000 characters. The fake engine stops one extraction and the first oversized merge at its reply cap, as Gemma's 1,200 tokens did.
        var meeting = Spoken(800);
        var numbered = MeetingNotesPrompt.Number(meeting);
        int chunks = MeetingNotesWriter.Chunks(numbered, MeetingNotesLimits.LocalChunkCharacters).Count();
        Assert.True(chunks >= 10, chunks.ToString());
        int extracts = 0, merges = 0, exhausted = 0, largestMerge = 0;
        var outcome = await Write(meeting, prompt =>
        {
            Assert.EndsWith(MeetingNotesPrompt.CapInstruction, prompt.System);
            if (IsMerge(prompt))
            {
                merges++;
                var lines = prompt.User.Split('\n').Where(l => l.StartsWith("POINT |", StringComparison.Ordinal)).ToArray();
                largestMerge = Math.Max(largestMerge, prompt.User.Length);
                if (lines.Length > 60) { exhausted++; throw Exhausted(); }
                return Task.FromResult("TITLE | Plan review\nSUMMARY | We reviewed alpha and bravo.\n" + string.Join('\n', lines.Take(30)));   // more than the caps: they are enforced
            }
            extracts++;
            if (extracts == 3) { exhausted++; throw Exhausted(); }
            var cited = prompt.User.Split('\n').First(l => l.StartsWith("[L", StringComparison.Ordinal));
            var number = int.Parse(cited[2..cited.IndexOf(' ')]);
            return Task.FromResult(string.Join('\n', Enumerable.Range(0, 12).Select(_ => $"POINT | L{number} | {Vocabulary[number % Vocabulary.Length]} plan")));
        }, chunkCharacters: MeetingNotesLimits.LocalChunkCharacters);
        Assert.Equal(chunks + 2, extracts);   // the stopped chunk is asked again as two halves
        Assert.True(merges >= 2);
        Assert.True(exhausted >= 2);
        Assert.True(outcome.Notes.KeyPoints.Count is > 0 and <= 8);
        Assert.NotNull(outcome.Notes.Title);
        Assert.True(largestMerge < 2 * MeetingNotesLimits.LocalChunkCharacters);
    }
}
