// Windows counterparts of the core checks in app/check_meetings.swift and app/check_speakers.swift.
using System.Text;
using Murmur.Core;

namespace Murmur.Core.Tests;

public sealed class MeetingTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "murmur-meetings-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) FileLinks.DeleteTree(root); }
    internal static Meeting NewMeeting(DateTime? date = null, MeetingSource? source = null) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        RecordedAt = date ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Source = source ?? MeetingSource.Everything,
    };

    [Fact]
    public void LateLineHidesEchoAndWordTimesMustMatch()
    {
        var meeting = NewMeeting();
        meeting.Insert(MeetingStream.Me, 16, 30, "Right. We ship the update next week", words: [[0, 1]]);
        meeting.Insert(MeetingStream.Them, 15, 29, "We ship the update next week");
        meeting.Insert(MeetingStream.Me, 0, 10, "We ship the update next week");
        meeting.Insert(MeetingStream.Me, 40, 44, "   ");
        Assert.Equal(new double[] { 0, 15, 16 }, meeting.Lines.Select(line => line.Start));
        Assert.Equal(30, meeting.Seconds);
        Assert.Null(meeting.Lines[0].Shown);
        Assert.Null(meeting.Lines[1].Shown);
        Assert.Equal("Right.", meeting.Lines[2].VisibleText);
        Assert.EndsWith("next week", meeting.Lines[2].Text);
        Assert.Null(meeting.Lines[2].Words);
    }

    [Fact]
    public void StoreQuarantinesBadRecordsAndExpiresFromRecordedDate()
    {
        var store = new MeetingStore(root);
        var old = NewMeeting(DateTime.UnixEpoch);
        var recent = NewMeeting();
        recent.Insert(MeetingStream.Me, 0, 1, "Synthetic line.");
        recent.Speakers = new(SpeakerState.Done) { EditedAt = recent.RecordedAt, Names = new() { [1] = "Speaker" } };
        store.Save(old);
        store.Save(recent);
        File.WriteAllText(Path.Combine(root, "bad.json"), "{");
        File.Copy(Path.Combine(root, recent.Id + ".json"), Path.Combine(root, "wrong-id.json"));
        var meetings = store.Meetings();
        Assert.Equal(new[] { recent.Id, old.Id }, meetings.Select(meeting => meeting.Id));
        Assert.Equal(2, Directory.EnumerateFiles(store.Unreadable).Count());
        Assert.Equal(recent.RecordedAt, meetings[0].Speakers!.EditedAt);
        Assert.True(meetings[0].Lines.Single().Text == "Synthetic line.", "line did not round-trip");
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, recent.Id + ".json")));
        Assert.Empty(store.Purge(null));
        Assert.Equal(new[] { old.Id }, store.Purge(old.RecordedAt));
        Assert.Throws<RetentionError>(() => store.Delete("../escape"));
    }

    [Fact]
    public void UnreadablePurgeNeverFollowsDirectoryOrFileLinks()
    {
        var store = new MeetingStore(Path.Combine(root, "meetings"));
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        var protectedFile = Path.Combine(outside, "old");
        File.WriteAllText(protectedFile, "outside");
        File.SetLastWriteTimeUtc(protectedFile, DateTime.UnixEpoch);
        if (OperatingSystem.IsWindows())
        {
            // Junction creation does not need Developer Mode or the symbolic-link privilege.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/d /c mklink /J \"{store.Unreadable}\" \"{outside}\"") { CreateNoWindow = true, RedirectStandardOutput = true, UseShellExecute = false })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(store.Unreadable, outside);
        store.PurgeUnreadable(DateTime.UtcNow);
        Assert.True(File.Exists(protectedFile));
        FileLinks.RemoveLink(store.Unreadable);

        Directory.CreateDirectory(store.Unreadable);
        var expired = Path.Combine(store.Unreadable, "expired");
        File.WriteAllText(expired, "synthetic");
        File.SetLastWriteTimeUtc(expired, DateTime.UnixEpoch);
        if (!OperatingSystem.IsWindows()) File.CreateSymbolicLink(Path.Combine(store.Unreadable, "linked"), protectedFile);
        store.PurgeUnreadable(DateTime.UtcNow);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(protectedFile));
        if (!OperatingSystem.IsWindows()) Assert.True(FileLinks.IsLink(Path.Combine(store.Unreadable, "linked")));
    }

    [Fact]
    public void StreamFeedRecoversBothStreamsInCaptureOrder()
    {
        var id = Guid.NewGuid().ToString("D");
        string folder = MeetingAudio.Folder(root, id);
        var jobs = new List<MeetingPump.Job>();
        var me = new MeetingStreamFeed(MeetingStream.Me, folder, 0.25, jobs.Add, () => Assert.Fail("write failed"));
        var them = new MeetingStreamFeed(MeetingStream.Them, folder, 1, jobs.Add, () => Assert.Fail("write failed"));
        for (int i = 0; i < 20; i++) me.Append(new float[16_000]);
        me.Finish();
        them.Append(new float[8000]);
        them.Finish();
        var recovered = MeetingStreamFeed.Recover(root, id).ToArray();
        Assert.Equal(3, recovered.Length);
        Assert.Equal(jobs.OrderBy(job => job.Start), recovered);
        Assert.Equal("me-250.wav", Path.GetFileName(recovered[0].Path));
        Assert.Equal(0.5, recovered[1].Duration);
        Assert.Equal(20, recovered.Where(job => job.Stream == MeetingStream.Me).Sum(job => job.Duration));
        Assert.Null(MeetingAudio.Parse("me--5.wav"));
        Assert.Equal("them-12346.wav", MeetingAudio.Name(MeetingStream.Them, 12.3456));
        Assert.Equal(0, MeetingAudio.Purge(root, new HashSet<string>(), new HashSet<string> { id }));
        Assert.NotEmpty(MeetingAudio.Leftovers(root, id));
        Assert.Equal(0, MeetingAudio.Purge(root, new HashSet<string>()));
        Assert.Empty(MeetingAudio.Leftovers(root, id));
    }

    [Fact]
    public async Task PumpRetriesFirstKeepsFailureAndPicksNextByCaptureTime()
    {
        var pending = System.Threading.Channels.Channel.CreateUnbounded<(string Path, TaskCompletionSource<ChunkText?> Reply)>();
        var delivered = System.Threading.Channels.Channel.CreateUnbounded<(double Start, ChunkText? Result)>();
        var pump = new MeetingPump(path =>
        {
            var reply = new TaskCompletionSource<ChunkText?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Writer.TryWrite((path, reply));
            return reply.Task;
        }, (job, result) => delivered.Writer.TryWrite((job.Start, result)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task Reply(string path, ChunkText? result)
        {
            var call = await pending.Reader.ReadAsync(timeout.Token);
            Assert.Equal(path, call.Path);
            call.Reply.SetResult(result);
        }
        pump.Enqueue(new(MeetingStream.Me, "zero", 0, 15));
        pump.Enqueue(new(MeetingStream.Me, "last", 30, 15));
        pump.Enqueue(new(MeetingStream.Them, "second", 15, 15));
        Assert.Equal(45, pump.Backlog);
        await Reply("zero", new("ok"));
        await Reply("second", null);
        await Reply("second", null);
        await Reply("last", new("ok"));
        Assert.Equal(0, (await delivered.Reader.ReadAsync(timeout.Token)).Start);
        var failure = await delivered.Reader.ReadAsync(timeout.Token);
        Assert.Equal(15, failure.Start);
        Assert.Null(failure.Result);
        Assert.Equal(30, (await delivered.Reader.ReadAsync(timeout.Token)).Start);
        pump.Stop();
    }

    [Fact]
    public void SpeakerSplitsKeepCitationsAndNamesRelabelPlainly()
    {
        var meeting = NewMeeting();
        meeting.Insert(MeetingStream.Them, 100, 115, "Can you send it? Yes I will.",
            words: [[0, 1], [1, 2], [2, 3], [3, 4], [6, 7], [7, 8], [8, 9]]);
        int cited = meeting.Lines[0].Id;
        meeting.Insert(MeetingStream.Me, 103, 105, "Right.");
        MeetingSpeakers.Apply([new("a", 100, 105), new("b", 106, 116)], meeting);
        Assert.Equal(new double[] { 100, 103, 106 }, meeting.Lines.Select(line => line.Start));
        Assert.Equal(new int?[] { 1, null, 2 }, meeting.Lines.Select(line => line.Speaker));
        Assert.Equal(cited, meeting.Lines[0].Id);
        Assert.All(meeting.Lines, line => Assert.Null(line.Words));
        meeting.Notes = new("Fixture", meeting.RecordedAt) { Summary = "Speaker 2 spoke." };
        meeting.RenameSpeaker(2, " Priya ");
        Assert.Equal("Priya spoke.", meeting.Notes.Summary);
        meeting.MergeSpeaker(2, 1);
        Assert.Equal("Speaker 1 spoke.", meeting.Notes.Summary);
        meeting.ReassignLine(cited, 3);
        Assert.True(meeting.Notes.Outdated);
        Assert.Equal("Sam, Speaker 10 and Sam's deck", MeetingSpeakers.Replace("Speaker 1", "Sam", "Speaker 1, Speaker 10 and Speaker 1's deck"));

        var segments = MeetingSpeakers.Segments(Encoding.UTF8.GetBytes("""
            {"segments":[{"speaker":"raw","start":0.5,"end":3,"embedding":[1]},{"speaker":"bad","start":2,"end":1}]}
            """));
        Assert.Equal(new SpeakerSegment("raw", 0.5, 3), Assert.Single(segments!));
        Assert.Null(MeetingSpeakers.Segments(Encoding.UTF8.GetBytes("not json")));
    }

    [Fact]
    public void StreamDamageAndLimitsUseElapsedTime()
    {
        var watch = new StreamWatch(0);
        Assert.Equal(StreamWatch.Action.None, watch.Update(100, 2).Kind);
        Assert.Equal(StreamWatch.Action.Stalled, watch.Update(100, 5).Kind);
        watch.Rebuilt(5);
        Assert.Equal(StreamWatch.Action.None, watch.Update(0, 7.9).Kind);
        Assert.Equal(StreamWatch.Action.Rebuild, watch.Update(0, 8).Kind);
        Assert.Equal((StreamWatch.Action.Recovered, 7d), watch.Update(10, 9));
        Assert.Equal(MeetingLimits.Action.WarnCap, MeetingLimits.Check(2 * 3600 + 51 * 60, 0));
        Assert.Equal(MeetingLimits.Action.StopCap, MeetingLimits.Check(3 * 3600, 0));
        Assert.Equal(MeetingLimits.Action.AskEnded, MeetingLimits.Check(600, 300));
        Assert.Equal(MeetingLimits.Action.StopSilent, MeetingLimits.Check(900, 600));
        var settings = new InMemorySettingsStore();
        Assert.True(MeetingConsent.Needed(settings));
        MeetingConsent.Acknowledge(settings);
        Assert.False(MeetingConsent.Needed(settings));
        Assert.Null(MeetingQuit.Check(false, 0));
        Assert.Equal("Stop and Quit", MeetingQuit.Check(true, 0)?.Confirm);
        Assert.Equal("Quit and Lose Them", MeetingQuit.Check(true, 2)?.Confirm);
    }
}
