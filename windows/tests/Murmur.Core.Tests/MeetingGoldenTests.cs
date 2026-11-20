using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public sealed class MeetingGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("meetings");
    public static IEnumerable<object[]> Escapes => GoldenFiles.Cases(Root, "escape", item => item.GetProperty("input").GetString()!);
    public static IEnumerable<object[]> Echoes => GoldenFiles.Cases(Root, "echo", item => item.GetProperty("text").GetString()!);

    [Theory, MemberData(nameof(Escapes))]
    public void MarkdownEscapeMatchesSwift(int index, string label)
    {
        Assert.NotEmpty(label);
        var item = Root.GetProperty("escape")[index];
        Assert.Equal(item.GetProperty("output").GetString(), MeetingExport.Escape(item.GetProperty("input").GetString()!));
    }

    [Theory, MemberData(nameof(Echoes))]
    public void EchoMatchesSwift(int index, string label)
    {
        Assert.NotEmpty(label);
        var item = Root.GetProperty("echo")[index];
        Assert.Equal(item.GetProperty("shown").GetString(), MeetingEcho.Shown(item.GetProperty("text").GetString()!, item.GetProperty("them").Deserialize<string[]>()!));
    }

    [Fact]
    public void SwiftRecordPromptAndExportsRoundTrip()
    {
        var document = Root.GetProperty("record");
        var meeting = document.Deserialize<Meeting>(MeetingStore.Json)!;
        Assert.True(JsonElement.DeepEquals(document, JsonSerializer.SerializeToElement(meeting, MeetingStore.Json)), "stored meeting schema differs");
        Assert.Equal(Root.GetProperty("markdown").GetString(), MeetingExport.Markdown(meeting));
        Assert.Equal(Root.GetProperty("transcript").GetString(), MeetingExport.Transcript(meeting));
        Assert.Equal(Root.GetProperty("notes").GetString(), MeetingExport.Notes(meeting));
        var prompt = MeetingNotesPrompt.Extract(MeetingNotesPrompt.Number(meeting), meeting.MyNotes, "a-b12!");
        Assert.Equal(Root.GetProperty("prompt").GetProperty("system").GetString(), prompt.System);
        Assert.Equal(Root.GetProperty("prompt").GetProperty("user").GetString(), prompt.User);
    }

    sealed record Corpus(Fixture[] Meetings);
    sealed record Fixture(string Id, string MyNotes, string[][] Lines, int[][] Decisions, Action[] Actions, string[] Traps);
    sealed record Action(int[] Lines, string? Owner);

    static string FindCorpus(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "app", file + ".json");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"Source fixture {file} was not found");
    }

    [Theory]
    [InlineData("meeting-notes-corpus")]
    [InlineData("meeting-notes-heldout")]
    public async Task FakeRepliesMatchSwiftGroundingAcrossCorpus(string file)
    {
        var expected = GoldenFiles.Load("meeting_notes_corpus");
        var corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllBytes(FindCorpus(file)), MeetingStore.Json)!;
        Assert.NotEmpty(corpus.Meetings);
        foreach (var fixture in corpus.Meetings)
        {
            var meeting = MeetingTests.NewMeeting();
            meeting.MyNotes = fixture.MyNotes;
            for (int i = 0; i < fixture.Lines.Length; i++)
                meeting.Insert(fixture.Lines[i][0] == "me" ? MeetingStream.Me : MeetingStream.Them, i * 20, i * 20 + 10, fixture.Lines[i][1]);
            var draft = new Draft
            {
                Points = fixture.Lines.Select((line, i) => new Draft.Item(line[1], [i + 1]))
                    .Concat(fixture.Traps.Select(trap => new Draft.Item(trap, [fixture.Lines.Length + 1]))).ToArray(),
                Decisions = fixture.Decisions.Select(ids => new Draft.Item(fixture.Lines[ids[0] - 1][1], ids)).ToArray(),
                Actions = fixture.Actions.Select(label => new Draft.Item(fixture.Lines[label.Lines[0] - 1][1], label.Lines, label.Owner)).ToArray(),
            };
            int calls = 0;
            var outcome = await MeetingNotesWriter.WriteAsync(meeting, MeetingNotesRoute.CustomProvider, "Fixture", (_, _) =>
            {
                calls++;
                return Task.FromResult(draft.Text);
            });
            Assert.Equal(1, calls);
            var actual = JsonSerializer.SerializeToElement(new
            {
                points = outcome.Notes.KeyPoints.Select(item => item.Lines),
                decisions = outcome.Notes.Decisions.Select(item => item.Lines),
                actions = outcome.Notes.Actions.Select(item => item.Lines),
                dropped = outcome.Notes.Dropped,
                reasons = outcome.Reasons.ToDictionary(reason => JsonNamingPolicy.CamelCase.ConvertName(reason.Key.ToString()), reason => reason.Value),
            });
            Assert.True(JsonElement.DeepEquals(expected.GetProperty(fixture.Id), actual), $"grounding differs for fixture {fixture.Id}");
        }
    }
}
