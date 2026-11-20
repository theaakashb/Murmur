// Port of app/MeetingRecorder.swift: one meeting at a time, the microphone and the call's audio chunked into one pump.
// Not ported here: speaker separation (diarization) and the cloud transcription route. Capture, clock, dispatch and the
// transcriber come in through the wiring, so the state machine runs under test without audio devices.
namespace Murmur.Core;

public sealed class MeetingCaptureException(string message) : Exception(message);

/// <summary>One capture stream (microphone, or the call's process loopback). Stop() ends the device, joins its thread and flushes the last samples to the callback.</summary>
public interface IMeetingStream
{
    /// <summary>Meter level 0..1 of the most recent audio.</summary>
    double Level { get; }
    /// <summary>Buffers delivered so far, for the stall watch.</summary>
    long Buffers { get; }
    /// <summary>Whether any non-zero sample has arrived (a stream without permission may deliver silence rather than an error).</summary>
    bool HeardSignal { get; }
    /// <summary>Whether anything this stream records is playing now; process loopback delivers nothing while it is not.</summary>
    bool IsPlaying { get; }
    /// <summary>Re-resolves the call app's processes. False when the stream could not be updated (it keeps what it had).</summary>
    bool Refresh();
    void Stop();
}

public interface IMeetingAudio
{
    /// <summary>False before the Windows build that has process loopback: meetings then record the microphone only.</summary>
    bool CallAudioSupported { get; }
    /// <summary>16 kHz mono blocks go to <paramref name="samples"/> on the capture thread. Throws <see cref="MeetingCaptureException"/>.</summary>
    IMeetingStream StartMicrophone(Action<float[]> samples);
    IMeetingStream StartCall(MeetingSource source, Action<float[]> samples);
    /// <summary>Whether the call app, or a helper below it, has a microphone session now.</summary>
    bool CallAppUsesMicrophone(string processName);
}

public sealed record MeetingRecorderWiring(
    MeetingStore? Store, string? AudioRoot, bool Persists, IMeetingAudio Audio,
    Func<string, CancellationToken, Task<ChunkText?>>? Transcribe, Func<double> Clock, Action<Action> Post,
    Func<string, string> ApplyDictionary, Func<MeetingNotesRoute?> NotesRoute,
    Func<Meeting, bool, CancellationToken, Task<MeetingNotesWriter.Outcome>>? NotesWriter, Func<bool> ConsentNeeded);

/// <summary>Everything runs on the thread that calls it. Pump results and stream failures come back through <c>Post</c>.</summary>
public sealed class MeetingRecorder
{
    public const string UnsavedWarning = "A meeting could not be saved to disk. Keep Murmur open and free up space; Murmur will keep trying. Copy the notes or transcript now if you need them.";

    MeetingRecorderWiring w = null!;
    readonly Dictionary<string, MeetingPump> pumps = [];
    readonly HashSet<string> askOnDone = [];
    IMeetingStream? me, them;
    string? folder;
    MeetingPump? livePump;
    double started, lastVoice, lastSave, lastRetry, lastRefresh, lastPlayingCheck;
    string? callApp;
    bool callWasUsingMic, themPlaying = true;
    StreamWatch meWatch = new(0), themWatch = new(0);
    MeterScale meScale = new(), themScale = new();
    bool Persists => w.Persists;

    public Meeting? Live { get; private set; }
    public List<Meeting> Meetings { get; private set; } = [];
    public double MeLevel { get; private set; }
    public double ThemLevel { get; private set; }
    public double Elapsed { get; private set; }
    public bool Behind { get; private set; }
    public string? Warning { get; private set; }
    public bool AskEnded { get; private set; }
    public HashSet<string> Finishing { get; } = [];
    public HashSet<string> Unsaved { get; } = [];
    public HashSet<string> NotesWriting { get; } = [];
    public Dictionary<string, int> Leftovers { get; private set; } = [];
    public Dictionary<string, string> NotesProblem { get; } = [];
    public MeetingSource? ConsentPending { get; set; }
    public string? SystemAudioProblem { get; set; }
    public string? Message { get; set; }
    public string? NotesChoicePending { get; set; }
    public event Action? Changed;
    public bool HasPendingUpdateWork => Live is not null || Finishing.Count > 0 || NotesWriting.Count > 0;

    /// <summary>The one place the recorder is wired. Finishes records a crash left open.</summary>
    public void Wire(MeetingRecorderWiring wiring)
    {
        w = wiring;
        if (w.AudioRoot is { } root) PrivateFiles.PrepareDirectory(root);
        if (w.Store is { } store)
            foreach (var meeting in Safe(store.Meetings) ?? [])
                if (!meeting.Finished && meeting.Id != Live?.Id) { meeting.Finished = true; Write(meeting); }
        Reload();
    }

    static T? Safe<T>(Func<T> read) where T : class
    {
        try { return read(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StoreError or RetentionError) { return null; }
    }

    public void Reload()
    {
        if (Persists)
        {
            var waiting = Meetings.Where(meeting => Unsaved.Contains(meeting.Id));
            var onDisk = (w.Store is { } store ? Safe(store.Meetings) ?? [] : []).Where(meeting => meeting.Id != Live?.Id && !Unsaved.Contains(meeting.Id));
            Meetings = waiting.Concat(onDisk).OrderByDescending(meeting => meeting.RecordedAt).ToList();
        }
        else Unsaved.Clear();
        PurgeAudio();
    }

    /// <summary>Launch recovery: transcribes the chunks a crash left, so the saved meeting is complete. Nothing falls back to the cloud.</summary>
    public void RecoverLeftovers() { foreach (var id in Leftovers.Keys.ToArray()) FinishLeftovers(id); }

    double Now => w.Clock();
    void Notify() => Changed?.Invoke();

    /// <summary>Starts recording, or asks for consent first. Returns false when nothing started.</summary>
    public bool Start(MeetingSource source)
    {
        if (Live is not null) { Message = "A meeting is already being recorded."; return false; }
        if (!w.Audio.CallAudioSupported) source = MeetingSource.MicrophoneOnly;
        if (w.ConsentNeeded()) { ConsentPending = source; return false; }
        if (w.AudioRoot is not { } root || w.Transcribe is null) { Message = "Meeting notes are unavailable: private storage could not be opened."; return false; }
        var meeting = new Meeting { Id = Guid.NewGuid().ToString("D"), RecordedAt = DateTime.UtcNow, Source = source };
        string dir;
        try { dir = MeetingAudio.Folder(root, meeting.Id); PrivateFiles.PrepareDirectory(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RetentionError)
        { Message = "Meeting notes could not create private storage."; return false; }
        var pump = MakePump(meeting.Id);
        started = lastVoice = lastSave = Now;
        SystemAudioProblem = null; Warning = null; AskEnded = false; Behind = false; Elapsed = 0; Message = null;
        callApp = source.BundleID; callWasUsingMic = false;
        meWatch = new(started); themWatch = new(started);
        meScale = new(); themScale = new(); themPlaying = true; lastPlayingCheck = 0;
        folder = dir; livePump = pump;
        Live = meeting;
        if (StartMe() is not null)
        {
            Live = null; folder = null; livePump = null; pumps.Remove(meeting.Id);
            try { FileLinks.DeleteTree(dir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Message = "The microphone could not start. Check Microphone access in Windows Settings > Privacy & security > Microphone.";
            return false;
        }
        if (!source.MicOnly && StartCall(source) is { } problem)
            SystemAudioProblem = $"The call's audio could not be recorded ({problem}). You can record only your microphone.";
        Save(true);
        Notify();
        return true;
    }

    MeetingStreamFeed Feed(MeetingStream stream) =>
        new(stream, folder!, Now - started, livePump!.Enqueue, () => w.Post(() => StreamFailed(stream)));

    string? StartMe()
    {
        if (folder is null || livePump is null) return "No meeting";
        StopStream(ref me);
        var feed = Feed(MeetingStream.Me);
        try { me = new FedStream(w.Audio.StartMicrophone(feed.Append), feed); return null; }
        catch (MeetingCaptureException ex) { return ex.Message; }
    }

    string? StartCall(MeetingSource source)
    {
        if (folder is null || livePump is null) return "No meeting";
        StopStream(ref them);
        var feed = Feed(MeetingStream.Them);
        try { them = new FedStream(w.Audio.StartCall(source, feed.Append), feed); return null; }
        catch (MeetingCaptureException ex) { return ex.Message; }
    }

    /// <summary>A stream and the feed behind it; stopping flushes the stream first and then the feed's last chunk.</summary>
    sealed class FedStream(IMeetingStream stream, MeetingStreamFeed feed) : IMeetingStream
    {
        public double Level => stream.Level;
        public long Buffers => stream.Buffers;
        public bool HeardSignal => stream.HeardSignal;
        public bool IsPlaying => stream.IsPlaying;
        public bool Refresh() => stream.Refresh();
        public void Stop() { stream.Stop(); feed.Finish(); }
    }

    static void StopStream(ref IMeetingStream? stream) { stream?.Stop(); stream = null; }

    public void ContinueWithMicrophoneOnly()
    {
        if (Live is not { } meeting) return;
        meeting.Source = meeting.Source with { MicOnly = true };
        SystemAudioProblem = null; Save(true);
    }

    public void Stop() => Finish(true);

    void Finish(bool manual)
    {
        if (Live is not { } meeting) return;
        StopStream(ref me); StopStream(ref them); folder = null; livePump = null;
        meeting.Seconds = Math.Max(meeting.Seconds, Now - started);
        meeting.Finished = true;
        Live = null;
        Meetings.Insert(0, meeting);
        Write(meeting);
        if (manual) askOnDone.Add(meeting.Id);
        if (pumps.TryGetValue(meeting.Id, out var pump) && !pump.IsIdle) Finishing.Add(meeting.Id); else pumps.Remove(meeting.Id);
        AskEnded = false; Warning = null; MeLevel = ThemLevel = 0;
        Notify(); RefreshLeftovers();
        if (!pumps.ContainsKey(meeting.Id)) TranscriptDone(meeting.Id);
    }

    public void Cancel()
    {
        if (Live is not { } meeting) return;
        StopStream(ref me); StopStream(ref them); folder = null; livePump = null;
        if (pumps.Remove(meeting.Id, out var pump)) pump.Stop();
        if (w.AudioRoot is { } root) try { FileLinks.DeleteTree(MeetingAudio.Folder(root, meeting.Id)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Unsaved.Remove(meeting.Id); Erase(meeting.Id);
        Live = null; AskEnded = false; Warning = null; SystemAudioProblem = null;
        Notify();
    }

    MeetingPump MakePump(string id)
    {
        var transcribe = w.Transcribe;
        var pump = new MeetingPump(path => transcribe is null ? Task.FromResult<ChunkText?>(null) : transcribe(path, CancellationToken.None),
            (job, text) => w.Post(() => Deliver(id, job, text)));
        pumps[id] = pump;
        return pump;
    }

    void Deliver(string id, MeetingPump.Job job, ChunkText? chunk)
    {
        if (chunk is null) { RefreshLeftovers(); Settle(id); return; }
        var spelled = w.ApplyDictionary(chunk.Text);
        Update(id, meeting =>
        {
            meeting.Insert(job.Stream, job.Start, job.Start + job.Duration, spelled, chunk.LocalFallback, chunk.Words);
            // A leftover finished after notes were written: they no longer cover the whole transcript.
            if (SwiftText.TrimWhitespaceAndNewlines(spelled).Length > 0) meeting.MarkNotesOutdated();
        });
        try { File.Delete(job.Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Settle(id);
    }

    Meeting? Any(string id) => Live?.Id == id ? Live : Meetings.FirstOrDefault(meeting => meeting.Id == id);

    void Settle(string id)
    {
        if (Live?.Id == id || pumps.TryGetValue(id, out var pump) && !pump.IsIdle) return;
        pumps.Remove(id); Finishing.Remove(id); RefreshLeftovers();
        TranscriptDone(id);
    }

    /// <summary>The transcript is complete: write notes on the chosen route, or ask for one the first time.</summary>
    void TranscriptDone(string id)
    {
        if (pumps.ContainsKey(id) || Finishing.Contains(id) || Meetings.FirstOrDefault(meeting => meeting.Id == id) is not { } meeting) return;
        askOnDone.Remove(id);
        if (meeting.Notes is not null || !meeting.Lines.Any(line => !line.IsHidden)) return;
        switch (w.NotesRoute())
        {
            case null: NotesChoicePending = id; Notify(); break;
            case MeetingNotesRoute.None: break;
            default: WriteNotes(id); break;
        }
    }

    /// <summary>Writes (or rewrites) a finished meeting's notes. Failure keeps the meeting and says why.</summary>
    public void WriteNotes(string id, bool lecture = false)
    {
        if (w.NotesWriter is not { } writer || NotesWriting.Contains(id) || Finishing.Contains(id) || Meetings.FirstOrDefault(m => m.Id == id) is not { } meeting) return;
        if (!meeting.Lines.Any(line => !line.IsHidden)) { NotesProblem[id] = "There is no transcript to write notes from."; return; }
        NotesWriting.Add(id); NotesProblem.Remove(id);
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await writer(meeting, lecture, CancellationToken.None).ConfigureAwait(false);
                w.Post(() => NotesDone(id, lecture, outcome, null));
            }
            catch (Exception ex) { w.Post(() => NotesDone(id, lecture, null, ex)); }
        });
    }

    void NotesDone(string id, bool lecture, MeetingNotesWriter.Outcome? outcome, Exception? error)
    {
        NotesWriting.Remove(id);
        if (outcome is null)
        {
            var reason = error switch
            {
                NotesFailure failure => failure.Message,
                _ => "Notes could not be written.",
            };
            NotesProblem[id] = reason + " The transcript is saved.";
            Notify();
            return;
        }
        Update(id, meeting =>
        {
            var notes = outcome.Notes;
            // Keep ticked action items that were written again word for word.
            var ticked = (meeting.Notes?.Actions ?? []).Where(item => item.Done == true).Select(item => item.Text).ToHashSet();
            if (!lecture) notes = notes with { Actions = notes.Actions.Select(item => ticked.Contains(item.Text) ? item with { Done = true } : item).ToArray() };
            if (lecture) meeting.LectureNotes = notes; else meeting.Notes = notes;
        });
        Notify();
    }

    public void NotesRouteChosen(MeetingNotesRoute route)
    {
        if (NotesChoicePending is not { } id) return;
        NotesChoicePending = null;
        if (route != MeetingNotesRoute.None) WriteNotes(id);
    }

    void Update(string id, Action<Meeting> change)
    {
        if (Live is { } live && live.Id == id) { change(live); Save(false); }
        else if (Meetings.FirstOrDefault(meeting => meeting.Id == id) is { } meeting) { change(meeting); Write(meeting); }
    }

    /// <summary>The live record is saved at most every 2 s (and on every forced save), so a crash keeps it.</summary>
    void Save(bool force)
    {
        if (!Persists || Live is not { } meeting) return;
        var now = Now;
        if (!force && now - lastSave < 2) return;
        lastSave = now;
        Write(meeting);
    }

    /// <summary>Every meeting write goes through here: a failure is remembered and shown, never dropped.</summary>
    void Write(Meeting meeting)
    {
        if (!Persists || w.Store is not { } store) return;
        try { store.Save(meeting); Unsaved.Remove(meeting.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StoreError or RetentionError) { Unsaved.Add(meeting.Id); }
    }

    void RetryUnsaved()
    {
        foreach (var id in Unsaved.ToArray()) { if (Any(id) is { } meeting) Write(meeting); else Unsaved.Remove(id); }
    }

    void Erase(string id)
    {
        if (w.Store is not { } store || !store.Exists(id)) return;
        try { store.Delete(id); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void SetNotes(string text, string id) => Update(id, meeting =>
    {
        if (meeting.MyNotes == text) return;
        meeting.MyNotes = text;
        meeting.MarkNotesOutdated();
    });

    public void ToggleAction(int index, string id) => Update(id, meeting =>
    {
        if (meeting.Notes is not { } notes || index < 0 || index >= notes.Actions.Count) return;
        meeting.Notes = notes with { Actions = notes.Actions.Select((item, i) => i == index ? item with { Done = !(item.Done ?? false) } : item).ToArray() };
    });

    /// <summary>A chunk file could not be written: mark the gap and rebuild the stream with a new chunker.</summary>
    void StreamFailed(MeetingStream stream)
    {
        if (Live is null) return;
        Gap();
        Rebuild(stream);
    }

    void Gap() => Live?.Gaps.Add(Elapsed);

    void Rebuild(MeetingStream stream)
    {
        if (Live is not { } meeting) return;
        if (stream == MeetingStream.Me) { StartMe(); meWatch.Rebuilt(Now); }
        else { StartCall(meeting.Source); themWatch.Rebuilt(Now); }
    }

    /// <summary>A stall marks one gap and rebuilds; until audio flows again it rebuilds every few seconds; recovery clears the warning.</summary>
    void Watch(MeetingStream stream, StreamWatch.Action action)
    {
        var name = stream == MeetingStream.Me ? "Your microphone" : "The call's audio";
        switch (action)
        {
            case StreamWatch.Action.Stalled:
                Gap(); Warning = $"{name} stopped arriving. Murmur is restarting it."; Rebuild(stream); break;
            case StreamWatch.Action.Rebuild: Rebuild(stream); break;
            case StreamWatch.Action.Recovered:
                if (Warning?.StartsWith(name, StringComparison.Ordinal) == true) Warning = null;
                break;
        }
    }

    /// <summary>Called about every <see cref="MeetingLimits.TickInterval"/> seconds by the UI timer.</summary>
    public void Tick()
    {
        var now = Now;
        if (Unsaved.Count > 0 && now - lastRetry >= 2) { lastRetry = now; RetryUnsaved(); }
        if (Live is not { } meeting) return;
        Elapsed = now - started;
        double meRaw = me?.Level ?? 0, themRaw = them?.Level ?? 0;
        MeLevel = meScale.Display(meRaw); ThemLevel = themScale.Display(themRaw);
        if (Math.Max(meRaw, themRaw) > MeetingLimits.SilenceLevel) { lastVoice = now; AskEnded = false; }
        Watch(MeetingStream.Me, meWatch.Update(me?.Buffers ?? 0, now).Kind);
        if (Live is null) return;
        if (!meeting.Source.MicOnly && SystemAudioProblem is null)
        {
            // Process loopback delivers nothing while no captured app plays (a call not yet connected): idle, not a stall.
            if (now - lastPlayingCheck >= 1) { lastPlayingCheck = now; themPlaying = them?.IsPlaying ?? true; }
            Watch(MeetingStream.Them, themWatch.Update(them?.Buffers ?? 0, now, !themPlaying).Kind);
        }
        Behind = (livePump?.Backlog ?? 0) > MeetingLimits.Behind;
        // A denied capture may deliver silence rather than an error: say so after 60 s without call audio.
        if (them is { } call && SystemAudioProblem is null && Elapsed > 60 && !call.HeardSignal && Warning is null)
            Warning = "No call audio yet. If the call is playing, Windows may be blocking Murmur from capturing it.";
        else if (them is { HeardSignal: true } && Warning?.StartsWith("No call audio", StringComparison.Ordinal) == true) Warning = null;
        if (now - lastRefresh >= 2)
        {
            lastRefresh = now;
            them?.Refresh();
            if (callApp is not null)
            {
                bool using_ = w.Audio.CallAppUsesMicrophone(callApp);
                if (callWasUsingMic && !using_) AskEnded = true;
                callWasUsingMic = using_;
            }
        }
        switch (MeetingLimits.Check(Elapsed, now - lastVoice))
        {
            case MeetingLimits.Action.StopCap: Message = "Meeting notes stopped at the 3-hour limit."; Finish(false); break;
            case MeetingLimits.Action.StopSilent: Message = "Meeting notes stopped after 10 minutes of silence."; Finish(false); break;
            case MeetingLimits.Action.WarnCap: Warning = $"Meeting notes stop at 3 hours, in {Math.Max(1, (int)((MeetingLimits.Cap - Elapsed) / 60))} min."; break;
            case MeetingLimits.Action.AskEnded: AskEnded = true; break;
        }
        Save(false);
        Notify();
    }

    public void RefreshLeftovers()
    {
        if (w.AudioRoot is not { } root) { Leftovers = []; return; }
        var found = new Dictionary<string, int>();
        foreach (var meeting in Meetings.Where(m => !pumps.ContainsKey(m.Id)))
        {
            int count = MeetingAudio.Leftovers(root, meeting.Id).Count;
            if (count > 0) found[meeting.Id] = count;
        }
        Leftovers = found;
    }

    public void FinishLeftovers(string id)
    {
        if (w.AudioRoot is not { } root || pumps.ContainsKey(id)) return;
        var files = MeetingAudio.Leftovers(root, id);
        if (files.Count == 0) return;
        var pump = MakePump(id);
        Finishing.Add(id); Leftovers.Remove(id);
        foreach (var job in MeetingStreamFeed.Recover(root, id)) pump.Enqueue(job);
    }

    public void Delete(string id)
    {
        if (Live?.Id == id) { Message = "Stop the meeting before deleting it."; return; }
        if (Persists && w.Store is { } store && store.Exists(id))
            try { store.Delete(id); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Message = "Could not delete this meeting."; return; }
        Unsaved.Remove(id);
        if (pumps.Remove(id, out var pump)) pump.Stop();
        Finishing.Remove(id); NotesProblem.Remove(id);
        if (NotesChoicePending == id) NotesChoicePending = null;
        if (w.AudioRoot is { } root) try { FileLinks.DeleteTree(MeetingAudio.Folder(root, id)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Meetings.RemoveAll(meeting => meeting.Id == id);
        RefreshLeftovers();
    }

    /// <summary>Expires meetings with History's retention, and chunk audio after <see cref="RetryWindow.Text"/>.</summary>
    public void Purge(DateTime? cutoff)
    {
        if (cutoff is { } at)
        {
            foreach (var meeting in Meetings.Where(m => m.RecordedAt <= at).ToArray())
            {
                if (pumps.Remove(meeting.Id, out var pump)) pump.Stop();
                Finishing.Remove(meeting.Id); Unsaved.Remove(meeting.Id);
                if (Persists) Erase(meeting.Id);
            }
            Meetings.RemoveAll(meeting => meeting.RecordedAt <= at);
            try { w.Store?.PurgeUnreadable(at); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        PurgeAudio();
    }

    void PurgeAudio()
    {
        if (w.AudioRoot is not { } root) return;
        var active = pumps.Keys.ToHashSet();
        if (Live is { } live) active.Add(live.Id);
        int undeleted = MeetingAudio.Purge(root, Meetings.Select(meeting => meeting.Id).ToHashSet(), active);
        RefreshLeftovers();
        if (undeleted > 0) Message = $"Murmur could not delete {undeleted} expired meeting audio {(undeleted == 1 ? "file" : "files")}. It will try again; you can also remove everything with Delete All Data.";
    }

    /// <summary>Asked before quitting. Tries the unsaved meetings once more first.</summary>
    public MeetingQuit.Warning? QuitWarning()
    {
        RetryUnsaved();
        return MeetingQuit.Check(Live is not null, Unsaved.Count);
    }

    /// <summary>Murmur is quitting: never keep recording. Untranscribed chunks stay for <see cref="RetryWindow.Text"/>.</summary>
    public void Shutdown()
    {
        if (Live is not null) Finish(false);
        RetryUnsaved();
        foreach (var pump in pumps.Values) pump.Stop();
    }
}
