// Port of the queue in app/Meetings.swift and the stream feed in app/MeetingCapture.swift.
namespace Murmur.Core;

public sealed class MeetingPump(Func<string, Task<ChunkText?>> transcribe, Action<MeetingPump.Job, ChunkText?> deliver)
{
    public sealed record Job(MeetingStream Stream, string Path, double Start, double Duration);
    public const int Attempts = 2;
    readonly Lock gate = new();
    readonly PriorityQueue<Job, (double Time, long Sequence)> queued = new();
    Job? inFlight;
    bool running, stopped;
    long sequence;

    public double Backlog { get { lock (gate) return (inFlight?.Duration ?? 0) + queued.UnorderedItems.Sum(item => item.Element.Duration); } }
    public bool IsIdle { get { lock (gate) return !running; } }

    public void Enqueue(Job job)
    {
        lock (gate)
        {
            if (stopped) return;
            queued.Enqueue(job, (job.Start, sequence++));
            if (running) return;
            running = true;
        }
        _ = PumpAsync();
    }

    public void Stop() { lock (gate) { stopped = true; queued.Clear(); } }

    async Task PumpAsync()
    {
        while (true)
        {
            Job job;
            lock (gate)
            {
                if (stopped || !queued.TryDequeue(out var next, out _))
                {
                    inFlight = null;
                    running = false;
                    return;
                }
                job = inFlight = next;
            }
            ChunkText? result = null;
            for (int attempt = 0; attempt < Attempts && result is null; attempt++)
            {
                lock (gate) if (stopped) break;
                try { result = await transcribe(job.Path).ConfigureAwait(false); }
                catch (Exception) { /* A failed chunk stays on disk for retry. */ }
            }
            lock (gate)
            {
                if (!stopped)
                {
                    try { deliver(job, result); }
                    catch (Exception) { /* The caller owns persistence. Leave the chunk for retry. */ }
                }
                inFlight = null;
            }
        }
    }
}

/// <summary>Feeds one stream's mono 16 kHz buffers, in order, to the shared meeting chunk queue.</summary>
public sealed class MeetingStreamFeed
{
    readonly Chunker chunker;

    public MeetingStreamFeed(MeetingStream stream, string folder, double offset, Action<MeetingPump.Job> enqueue, Action failed)
    {
        if (FileLinks.IsLink(folder)) throw new RetentionError("Meeting audio requires a real directory");
        PrivateFiles.PrepareDirectory(folder);
        double next = offset;
        chunker = new(chunk =>
        {
            double duration = (double)chunk.Samples.Length / AudioFormat.SampleRate;
            double start = next;
            next += duration;
            var path = Path.Combine(folder, MeetingAudio.Name(stream, start));
            try
            {
                if (FileLinks.Exists(path)) throw new RetentionError("Meeting audio already exists");
                PrivateFiles.Write(WAVAudio.EncodePCM16(chunk.Samples, AudioFormat.SampleRate), path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StoreError or RetentionError)
            {
                failed();
                return;
            }
            enqueue(new(stream, path, start, duration));
        });
    }

    public void Append(float[] samples) => chunker.Append(samples);
    public void Finish() => chunker.Finish();

    public static double? Seconds(string path)
    {
        try
        {
            if (FileLinks.IsLink(path)) return null;
            return WAVAudio.Decode(File.ReadAllBytes(path)) is { } audio ? (double)audio.Samples.Length / audio.SampleRate : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static IEnumerable<MeetingPump.Job> Recover(string root, string id)
    {
        foreach (var path in MeetingAudio.Leftovers(root, id))
        {
            var (stream, start) = MeetingAudio.Parse(path)!.Value;
            yield return new(stream, path, start, Seconds(path) ?? 0);
        }
    }
}
