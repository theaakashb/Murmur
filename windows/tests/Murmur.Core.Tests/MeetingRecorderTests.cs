using System.Collections.Concurrent;
using Murmur.Core;

namespace Murmur.Core.Tests;

public sealed class MeetingRecorderTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "murmur-recorder-" + Guid.NewGuid().ToString("N"));
    readonly ConcurrentQueue<Action> posted = new();
    readonly FakeAudio audio = new();
    double clock = 1000;
    bool consent;
    MeetingNotesRoute? route = MeetingNotesRoute.None;
    Func<Meeting, bool, CancellationToken, Task<MeetingNotesWriter.Outcome>>? notes;
    readonly List<string> transcribed = [];

    sealed class FakeStream(Action<float[]> samples) : IMeetingStream
    {
        public Action<float[]> Samples { get; } = samples;
        public double Level { get; set; }
        public long Buffers { get; set; }
        public bool HeardSignal { get; set; } = true;
        public bool IsPlaying { get; set; } = true;
        public int Refreshes, Stops;
        public bool Refresh() { Refreshes++; return true; }
        public void Stop() => Stops++;
    }

    sealed class FakeAudio : IMeetingAudio
    {
        public bool CallAudioSupported { get; set; } = true;
        public string? MicError, CallError;
        public bool MicUse;
        public List<FakeStream> Mics { get; } = [];
        public List<FakeStream> Calls { get; } = [];
        public IMeetingStream StartMicrophone(Action<float[]> samples)
        {
            if (MicError is not null) throw new MeetingCaptureException(MicError);
            var stream = new FakeStream(samples); Mics.Add(stream); return stream;
        }
        public IMeetingStream StartCall(MeetingSource source, Action<float[]> samples)
        {
            if (CallError is not null) throw new MeetingCaptureException(CallError);
            var stream = new FakeStream(samples); Calls.Add(stream); return stream;
        }
        public bool CallAppUsesMicrophone(string processName) => MicUse;
    }

    MeetingRecorder Make(bool persists = true)
    {
        var recorder = new MeetingRecorder();
        recorder.Wire(new(new MeetingStore(Path.Combine(root, "meetings")), Path.Combine(root, "audio"), persists, audio,
            (path, _) => { lock (transcribed) transcribed.Add(Path.GetFileName(path)); return Task.FromResult<ChunkText?>(new("hello there")); },
            () => clock, posted.Enqueue, text => text, () => route, (meeting, lecture, _) => notes!(meeting, lecture, default), () => consent));
        return recorder;
    }

    static float[] Tone(double seconds) => Enumerable.Range(0, (int)(seconds * AudioFormat.SampleRate)).Select(i => (float)(0.3 * Math.Sin(i * 0.05))).ToArray();

    void Drain(Func<bool> until)
    {
        for (var stop = DateTime.UtcNow.AddSeconds(10); !until() && DateTime.UtcNow < stop; Thread.Sleep(5))
            while (posted.TryDequeue(out var action)) action();
        while (posted.TryDequeue(out var action)) action();
        Assert.True(until());
    }

    [Fact]
    public void ConsentComesFirstAndNothingStartsWithoutIt()
    {
        consent = true;
        var recorder = Make();
        Assert.False(recorder.Start(MeetingSource.Everything));
        Assert.Equal(MeetingSource.Everything, recorder.ConsentPending);
        Assert.Empty(audio.Mics);
        consent = false;
        Assert.True(recorder.Start(MeetingSource.Everything));
    }

    [Fact]
    public void RecordsBothStreamsTranscribesChunksAndSavesTheFinishedMeeting()
    {
        var recorder = Make();
        Assert.True(recorder.Start(new MeetingSource("Zoom", "zoom")));
        Assert.Single(audio.Mics); Assert.Single(audio.Calls);
        audio.Mics[0].Samples(Tone(2)); audio.Calls[0].Samples(Tone(2));
        clock += 5;
        recorder.Stop();
        Assert.Null(recorder.Live);
        Drain(() => recorder.Meetings[0].Lines.Count == 2);
        var meeting = recorder.Meetings[0];
        Assert.True(meeting.Finished);
        Assert.Equal([MeetingStream.Me, MeetingStream.Them], meeting.Lines.Select(line => line.Stream).Order());
        Assert.Equal("hello there", meeting.Lines[0].Text);
        Assert.Empty(MeetingAudio.Leftovers(Path.Combine(root, "audio"), meeting.Id));
        Assert.Single(new MeetingStore(Path.Combine(root, "meetings")).Meetings());
        Assert.True(new MeetingStore(Path.Combine(root, "meetings")).Meetings()[0].Finished);
    }

    [Fact]
    public void AMicrophoneThatCannotStartCancelsTheMeetingAndLeavesNoFolder()
    {
        audio.MicError = "boom";
        var recorder = Make();
        Assert.False(recorder.Start(MeetingSource.Everything));
        Assert.Null(recorder.Live);
        Assert.Contains("microphone", recorder.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "audio")));
    }

    [Fact]
    public void CallAudioFailureKeepsTheMeetingAndOffersMicrophoneOnly()
    {
        audio.CallError = "not supported";
        var recorder = Make();
        Assert.True(recorder.Start(MeetingSource.Everything));
        Assert.Contains("not supported", recorder.SystemAudioProblem);
        recorder.ContinueWithMicrophoneOnly();
        Assert.Null(recorder.SystemAudioProblem);
        Assert.True(recorder.Live!.Source.MicOnly);
    }

    [Fact]
    public void WithoutProcessLoopbackMeetingsRecordTheMicrophoneOnly()
    {
        audio.CallAudioSupported = false;
        var recorder = Make();
        Assert.True(recorder.Start(MeetingSource.Everything));
        Assert.True(recorder.Live!.Source.MicOnly);
        Assert.Empty(audio.Calls);
    }

    [Fact]
    public void AStalledMicrophoneMarksOneGapAndRestartsUntilAudioReturns()
    {
        var recorder = Make();
        recorder.Start(MeetingSource.MicrophoneOnly);
        audio.Mics[0].Buffers = 5;
        clock += 1; recorder.Tick();
        Assert.Single(audio.Mics);
        clock += 4; recorder.Tick();
        Assert.Equal(2, audio.Mics.Count);
        Assert.Single(recorder.Live!.Gaps);
        Assert.StartsWith("Your microphone stopped", recorder.Warning);
        audio.Mics[1].Buffers = 1;
        clock += 0.2; recorder.Tick();
        Assert.Null(recorder.Warning);
        Assert.Equal(1, audio.Mics[0].Stops);
    }

    [Fact]
    public void ANotYetPlayingCallIsIdleNotAStall()
    {
        var recorder = Make();
        recorder.Start(new MeetingSource("Zoom", "zoom"));
        audio.Calls[0].IsPlaying = false;
        for (int i = 0; i < 20; i++) { clock += 1; audio.Mics[0].Buffers++; recorder.Tick(); }
        Assert.Single(audio.Calls);
        Assert.Empty(recorder.Live!.Gaps);
    }

    [Fact]
    public void TheCallAppReleasingTheMicrophoneAsksWhetherTheMeetingEnded()
    {
        var recorder = Make();
        recorder.Start(new MeetingSource("Zoom", "zoom"));
        audio.MicUse = true;
        clock += 2; audio.Mics[0].Buffers++; audio.Calls[0].Buffers++; recorder.Tick();
        Assert.False(recorder.AskEnded);
        audio.MicUse = false;
        clock += 2; audio.Mics[0].Buffers++; audio.Calls[0].Buffers++; recorder.Tick();
        Assert.True(recorder.AskEnded);
    }

    [Fact]
    public void TenMinutesOfSilenceStopsAndSavesTheMeeting()
    {
        var recorder = Make();
        recorder.Start(MeetingSource.MicrophoneOnly);
        for (int i = 0; i < 61; i++) { clock += 10; audio.Mics[0].Buffers++; recorder.Tick(); }
        Assert.Null(recorder.Live);
        Assert.Contains("silence", recorder.Message);
        Assert.True(recorder.Meetings[0].Finished);
    }

    [Fact]
    public void ACrashLeavesAnOpenMeetingAndChunksThatTheNextLaunchFinishes()
    {
        var first = Make();
        first.Start(MeetingSource.MicrophoneOnly);
        audio.Mics[0].Samples(Tone(25)); // one full chunk written, the rest still buffered in the dead process
        var id = first.Live!.Id;
        Assert.NotEmpty(MeetingAudio.Leftovers(Path.Combine(root, "audio"), id));
        Assert.False(new MeetingStore(Path.Combine(root, "meetings")).Meetings().Single().Finished);

        var relaunched = Make();
        Assert.True(relaunched.Meetings.Single().Finished);
        Assert.Equal(1, relaunched.Leftovers[id]);
        relaunched.RecoverLeftovers();
        Drain(() => relaunched.Meetings[0].Lines.Count == 1);
        Assert.Empty(MeetingAudio.Leftovers(Path.Combine(root, "audio"), id));
        Assert.True(new MeetingStore(Path.Combine(root, "meetings")).Meetings().Single().Lines.Count == 1);
    }

    [Fact]
    public void NotesAreWrittenOnTheChosenRouteAndAFailureKeepsTheTranscript()
    {
        route = MeetingNotesRoute.CustomProvider;
        notes = (meeting, _, _) => Task.FromResult(new MeetingNotesWriter.Outcome(new MeetingNotes("Custom Provider", DateTime.UtcNow) { Title = "T" }, new Dictionary<NotesGrounding.Reason, int>()));
        var recorder = Make();
        recorder.Start(MeetingSource.MicrophoneOnly);
        audio.Mics[0].Samples(Tone(2));
        recorder.Stop();
        Drain(() => recorder.Meetings[0].Notes is not null);
        Assert.Equal("T", recorder.Meetings[0].Notes!.Title);

        notes = (_, _, _) => throw new NotesFailure("Set up a Custom Provider to write meeting notes.");
        var id = recorder.Meetings[0].Id;
        recorder.Meetings[0].Notes = null;
        recorder.WriteNotes(id);
        Drain(() => recorder.NotesProblem.ContainsKey(id));
        Assert.Contains("The transcript is saved.", recorder.NotesProblem[id]);
        Assert.Single(recorder.Meetings[0].Lines);
    }

    [Fact]
    public void WithNoRouteChosenTheFirstStopAsks()
    {
        route = null;
        var recorder = Make();
        recorder.Start(MeetingSource.MicrophoneOnly);
        audio.Mics[0].Samples(Tone(2));
        recorder.Stop();
        Drain(() => recorder.NotesChoicePending is not null);
        Assert.Equal(recorder.Meetings[0].Id, recorder.NotesChoicePending);
    }

    [Fact]
    public void CancelAndDeleteRemoveTheMeetingAndItsAudio()
    {
        var recorder = Make();
        recorder.Start(MeetingSource.MicrophoneOnly);
        var id = recorder.Live!.Id;
        recorder.Delete(id);
        Assert.NotNull(recorder.Live); // refused while recording
        recorder.Cancel();
        Assert.Null(recorder.Live);
        Assert.False(Directory.Exists(MeetingAudio.Folder(Path.Combine(root, "audio"), id)));
        Assert.Empty(new MeetingStore(Path.Combine(root, "meetings")).Meetings());
    }

    [Fact]
    public void QuitWhileRecordingWarnsThenShutdownStopsAndSaves()
    {
        var recorder = Make();
        recorder.Start(MeetingSource.MicrophoneOnly);
        Assert.NotNull(recorder.QuitWarning());
        recorder.Shutdown();
        Assert.Null(recorder.Live);
        Assert.True(new MeetingStore(Path.Combine(root, "meetings")).Meetings().Single().Finished);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }
}
