// Ports the UndoWindow assertions of app/check_recording.swift, and exercises CaptureSession (Session.swift) with a fake store and timer.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class SessionTests
{
    [Fact]
    public void UndoWindowHandsTheItemOverOnceInsideItsLifetime()
    {
        var c = new Checker();
        var clock = DateTime.UnixEpoch.AddSeconds(1_000);
        var window = new UndoWindow<float[]>(5) { Now = () => clock };
        c.Check(!window.IsOpen && window.Take() is null, "nothing held at first");
        window.Hold([0.1f, 0.2f]);
        clock = clock.AddSeconds(4.9);
        c.Check(window.IsOpen && window.Take() is [0.1f, 0.2f], "taken inside the window");
        c.Check(window.Take() is null && !window.IsOpen, "taken only once");
        window.Hold([0.3f]); clock = clock.AddSeconds(5);
        c.Check(!window.IsOpen && window.Take() is null, "nothing after the window");
        window.Hold([0.4f]); window.Discard();
        c.Check(!window.IsOpen && window.Take() is null, "discard releases it");
        window.Hold([0.5f]); clock = clock.AddSeconds(6);
        c.Check(window.Take() is null && !window.IsOpen, "an expired item is released by the take");
        c.Done();
    }

    sealed class FakeStore : IRetryAudioStore
    {
        public int Created; public readonly List<string> Deleted = [];
        public string Create() => $"audio-{++Created}.wav";
        public void Delete(string path) => Deleted.Add(path);
    }

    sealed class FakeClock
    {
        public readonly List<(double Delay, Action Action, bool[] Cancelled)> Timers = [];
        public IDisposable Schedule(double delay, Action action)
        {
            var cancelled = new bool[1];
            Timers.Add((delay, action, cancelled));
            return new Cancel(cancelled);
        }
        sealed class Cancel(bool[] flag) : IDisposable { public void Dispose() => flag[0] = true; }
        public void Fire(int index) { if (!Timers[index].Cancelled[0]) Timers[index].Action(); }
    }

    sealed class Rig(CaptureSession session, FakeStore store, FakeClock clock, List<string> log)
    {
        public Action? Pending;
        public void Deconstruct(out CaptureSession s, out FakeStore st, out FakeClock cl, out List<string> l) { s = session; st = store; cl = clock; l = log; }
    }

    static Rig Make(bool startOk = true, bool stopOk = true)
    {
        var store = new FakeStore(); var clock = new FakeClock(); var log = new List<string>();
        Rig? rig = null;
        var session = new CaptureSession(store, url => { log.Add("start " + url); return startOk; }, () => { log.Add("stop"); return stopOk; },
            (url, done) => { log.Add("process " + url); log.Add("processing"); rig!.Pending = done; }) { Schedule = clock.Schedule };
        return rig = new Rig(session, store, clock, log);
    }

    [Fact]
    public void StartFinishAndProcess()
    {
        var c = new Checker();
        var rig = Make();
        var (session, store, clock, log) = rig;
        var completed = 0;
        session.Receive(ShortcutAction.Start, () => completed++);
        c.Check(session.State == CaptureState.Listening && session.Audio == "audio-1.wav" && clock.Timers[0].Delay == CaptureSession.RecordingLimit, "start listens with the five-minute limit");
        session.Receive(ShortcutAction.Start, () => completed++);
        c.Check(store.Created == 1, "a second start while listening does nothing");
        session.Receive(ShortcutAction.Finish, () => completed++);
        c.Check(session.State == CaptureState.Processing && clock.Timers[0].Cancelled[0] && log.SequenceEqual(["start audio-1.wav", "stop", "process audio-1.wav", "processing"]), "finish stops capture and processes the audio");
        rig.Pending!();
        c.Check(session.State == CaptureState.Idle && session.Audio is null && completed == 1, "done frees the session and completes once");
        session.Receive(ShortcutAction.Finish, () => completed++);
        c.Check(completed == 2, "finish when not listening just completes");
        c.Done();
    }

    [Fact]
    public void CancelDiscardsTheRecordingAfterTheUndoHookSeesIt()
    {
        var c = new Checker();
        var (session, store, _, _) = Make();
        string? seen = null;
        session.WillDeleteCanceled = path => seen = path;
        session.Receive(ShortcutAction.Start, () => { });
        session.Receive(ShortcutAction.Cancel, () => { });
        c.Check(seen == "audio-1.wav" && store.Deleted.SequenceEqual(["audio-1.wav"]) && session.State == CaptureState.Idle, "cancel hands the file to Undo, then deletes it");
        c.Done();
    }

    [Fact]
    public void AFailedStartDeletesTheFileResetsTheShortcutAndCompletes()
    {
        var (session, store, _, _) = Make(startOk: false);
        int reset = 0, completed = 0;
        session.ResetShortcut = () => reset++;
        session.Receive(ShortcutAction.Start, () => completed++);
        Assert.True(session.State == CaptureState.Idle && store.Deleted.SequenceEqual(["audio-1.wav"]) && reset == 1 && completed == 1);
    }

    [Fact]
    public void ACaptureThatStopsEmptyDiscardsTheAudio()
    {
        var (session, store, _, _) = Make(stopOk: false);
        int completed = 0;
        session.Receive(ShortcutAction.Start, () => { });
        session.Receive(ShortcutAction.Finish, () => completed++);
        Assert.True(session.State == CaptureState.Idle && store.Deleted.SequenceEqual(["audio-1.wav"]) && completed == 1);
    }

    [Fact]
    public void ReleaseTailKeepsCapturingThenProcesses()
    {
        var c = new Checker();
        var (session, _, clock, log) = Make();
        session.ReleaseTail = 0.3;
        session.Uptime = () => 10;
        session.ReleasedAt = 9.9; // released 0.1 s ago: 0.2 s of tail left
        session.Receive(ShortcutAction.Start, () => { });
        session.Receive(ShortcutAction.Finish, () => { });
        c.Check(session.State == CaptureState.Processing && !log.Contains("stop") && Math.Abs(clock.Timers[1].Delay - 0.2) < 1e-9 && session.ReleasedAt is null, "the tail runs to release plus the tail, and the release time is consumed");
        clock.Fire(1);
        c.Check(log.Contains("stop") && log.Contains("process audio-1.wav"), "when the tail ends capture stops and processing starts");
        c.Done();
    }

    [Fact]
    public void ACaptureStartedDuringProcessingIsAdoptedByTheNextStart()
    {
        var c = new Checker();
        var rig = Make();
        var (session, store, clock, _) = rig;
        var now = DateTime.UnixEpoch.AddSeconds(500);
        session.Now = () => now;
        c.Check(!session.StartEarly(), "no early capture while idle");
        session.Receive(ShortcutAction.Start, () => { });
        session.Receive(ShortcutAction.Finish, () => { });
        c.Check(session.StartEarly() && session.Early == "audio-2.wav" && !session.StartEarly(), "an early capture starts once during processing");
        rig.Pending!();
        now = now.AddSeconds(100);
        session.Receive(ShortcutAction.Start, () => { });
        c.Check(session.State == CaptureState.Listening && session.Audio == "audio-2.wav" && session.Early is null && store.Created == 2, "the next start adopts it");
        c.Check(Math.Abs(clock.Timers[^1].Delay - (CaptureSession.RecordingLimit - 100)) < 1e-9, "it counts from its own start, so it never records past the limit");
        session.Shutdown();
        c.Check(store.Deleted.Contains("audio-2.wav") && session.State == CaptureState.Idle, "shutdown stops and discards a recording in progress");
        c.Done();
    }

    [Fact]
    public void CancelDuringProcessingDropsTheEarlyCapture()
    {
        var (session, store, _, _) = Make();
        session.Receive(ShortcutAction.Start, () => { });
        session.Receive(ShortcutAction.Finish, () => { });
        Assert.True(session.StartEarly());
        session.Receive(ShortcutAction.Cancel, () => { });
        Assert.True(session.Early is null && store.Deleted.SequenceEqual(["audio-2.wav"]));
    }
}
