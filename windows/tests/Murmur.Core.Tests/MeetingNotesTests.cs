using Murmur.Core;

namespace Murmur.Core.Tests;

public sealed class MeetingNotesTests
{
    [Fact]
    public async Task WriterSplitsOnProviderOverflowAndGroundsTheMerge()
    {
        var meeting = MeetingTests.NewMeeting();
        meeting.Insert(MeetingStream.Them, 0, 10, "The quarterly roadmap covers migration planning.");
        meeting.Insert(MeetingStream.Me, 20, 30, "I'll send the migration roadmap by Friday.");
        int maps = 0, merges = 0;
        var outcome = await MeetingNotesWriter.WriteAsync(meeting, MeetingNotesRoute.CustomProvider, "Synthetic", (prompt, _) =>
        {
            if (prompt.System.StartsWith("You merge", StringComparison.Ordinal))
            {
                merges++;
                Assert.Contains("POINT | L1 | Quarterly roadmap", prompt.User);
                return Task.FromResult("TITLE | Quarterly roadmap\nPOINT | L1 L2 | Migration roadmap\nPOINT | L3 | Roadmap");
            }
            if (++maps == 1) throw new ProviderException(ProviderError.PayloadTooLarge(ProviderID.CustomAPI, null));
            return Task.FromResult(prompt.User.Contains("[L1", StringComparison.Ordinal)
                ? "POINT | L1 | Quarterly roadmap" : "ACTION | L2 | Me | Friday | Send the migration roadmap");
        }, nonce: () => "a-b12!");
        Assert.Equal(3, maps);
        Assert.Equal(1, merges);
        Assert.Equal("Quarterly roadmap", outcome.Notes.Title);
        Assert.Equal(new[] { 1, 2 }, Assert.Single(outcome.Notes.KeyPoints).Lines);
        Assert.Equal(1, outcome.Reasons[NotesGrounding.Reason.UnknownLine]);

        var disabled = await MeetingNotesWriter.WriteAsync(meeting, MeetingNotesRoute.None, "None",
            (_, _) => throw new InvalidOperationException("None called the provider"));
        Assert.True(disabled.Notes.IsEmpty);
        var failed = await Assert.ThrowsAsync<NotesFailure>(() => MeetingNotesWriter.WriteAsync(meeting,
            MeetingNotesRoute.CustomProvider, "Synthetic", (_, _) => throw new ProviderException(ProviderError.PayloadTooLarge(ProviderID.CustomAPI, null))));
        Assert.Equal("Part of this meeting is too long for the notes route.", failed.Message);
    }

    [Theory]
    [InlineData("Northern states expected to be cooler than average", "No states are expected to be cooler than average.", true)]
    [InlineData("Next week is warmer", "Next week will be cooler.", true)]
    [InlineData("Launch the beta Friday", "We can't launch the beta Thursday, let's do Friday.", false)]
    [InlineData("Deck due Friday", "Send the deck no later than Friday.", false)]
    public void MatchesSwiftPolarityAssertions(string claim, string cited, bool changed) =>
        Assert.Equal(changed, NotesGrounding.PolarityChanged(claim, cited));

    [Fact]
    public void SearchIncludesLectureNotesAndRestOfLocalDay()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
        var date = new DateTime(2026, 11, 2, 4, 30, 0, DateTimeKind.Utc);
        var day = new DateTime(2026, 11, 1);
        var meeting = MeetingTests.NewMeeting(date, MeetingSource.MicrophoneOnly);
        meeting.MyNotes = "Café budget";
        meeting.LectureNotes = new("Synthetic", date) { KeyPoints = [new("Migration timeline", [1])] };
        Assert.True(meeting.Matches("cafe", day, day, zone));
        Assert.True(meeting.Matches("MIGRATION", day, day, zone));
        Assert.False(meeting.Matches("migration", day.AddDays(1), zone: zone));
        Assert.False(meeting.Matches("", through: day.AddDays(-1), zone: zone));
    }
}
