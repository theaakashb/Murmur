using System.Diagnostics;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.App;

/// <summary>A running capture: the microphone, or the synthetic test source. Stop returns everything as 16 kHz mono and closes the retry WAV.</summary>
internal interface IRecorder
{
    AudioCaptureResult Stop();
    Exception? Failure { get; }
}

/// <summary>Retains the key-down blocks until the controller accepts the session and chooses its consumers.</summary>
internal sealed class RecordingFeed
{
    readonly object gate = new();
    List<float[]>? early = [];
    Action<float[]>? handler;

    public void Push(float[] samples)
    {
        lock (gate)
        {
            if (early is not null) early.Add(samples);
            else handler?.Invoke(samples);
        }
    }

    public void Attach(Action<float[]>? consume)
    {
        lock (gate)
        {
            if (consume is not null && early is not null)
                foreach (var samples in early) consume(samples);
            early = null;
            handler = consume;
        }
    }
}

internal sealed class MicRecorder(AudioCapture capture) : IRecorder
{
    public static IRecorder Start(string? deviceId, string wavPath, Action<float> onLevel, Action<float[]> onSamples) =>
        new MicRecorder(AudioCapture.Start(string.IsNullOrEmpty(deviceId) ? null : deviceId, wavPath, onSamples: onSamples, onLevel: onLevel));
    public AudioCaptureResult Stop() => capture.Stop();
    public Exception? Failure => capture.Failure;
}

/// <summary>
/// MURMUR_SYNTHETIC_AUDIO: reads one WAV named by next.txt and feeds it in real time through the same AudioConverter, level callback and
/// retry-WAV path as the microphone; silence after it ends. PCM16 or Float32 WAVs only. A missing next.txt or WAV fails like "no microphone".
/// </summary>
internal sealed class SyntheticRecorder : IRecorder
{
    const double ChunkSeconds = 0.02;
    readonly byte[] pcm;
    readonly int rate, channels, bits;
    readonly bool isFloat;
    readonly AudioConverter converter;
    readonly FileStream file;
    readonly Action<float> onLevel;
    readonly Action<float[]> onSamples;
    readonly List<float> samples = [];
    readonly Thread thread;
    readonly string wavPath;
    volatile bool stopping;
    long dataBytes;

    public Exception? Failure => null;

    public static IRecorder Start(string folder, string wavPath, Action<float> onLevel, Action<float[]> onSamples) => new SyntheticRecorder(folder, wavPath, onLevel, onSamples);

    SyntheticRecorder(string folder, string wavPath, Action<float> onLevel, Action<float[]> onSamples)
    {
        this.wavPath = wavPath; this.onLevel = onLevel; this.onSamples = onSamples;
        byte[] wav;
        try { wav = File.ReadAllBytes(File.ReadAllText(Path.Combine(folder, "next.txt")).Trim()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new AudioCaptureException(AudioCaptureError.NoDevice, "No microphone was found."); }
        (pcm, rate, channels, bits, isFloat) = Parse(wav);
        converter = new AudioConverter(rate, channels, isFloat ? SampleKind.Float32 : SampleKind.Pcm16);
        file = new FileStream(wavPath, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read });
        WriteHeader();
        thread = new Thread(Run) { IsBackground = true, Name = "Murmur synthetic audio" };
        thread.Start();
    }

    static (byte[] Data, int Rate, int Channels, int Bits, bool IsFloat) Parse(byte[] b)
    {
        static AudioCaptureException Bad() => new(AudioCaptureError.UnsupportedFormat, "The test audio file is not a supported WAV.");
        if (b.Length < 12 || b[0] != 'R' || b[8] != 'W') throw Bad();
        int tag = 0, ch = 0, rate = 0, bits = 0, pos = 12;
        while (pos + 8 <= b.Length)
        {
            int size = BitConverter.ToInt32(b, pos + 4), body = pos + 8;
            var id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            if (id == "fmt " && size >= 16) { tag = BitConverter.ToInt16(b, body); ch = BitConverter.ToInt16(b, body + 2); rate = BitConverter.ToInt32(b, body + 4); bits = BitConverter.ToInt16(b, body + 14); }
            else if (id == "data")
            {
                var length = Math.Max(0, Math.Min(size < 0 ? int.MaxValue : size, b.Length - body));
                if (ch <= 0 || rate <= 0 || !((tag == 1 && bits == 16) || (tag == 3 && bits == 32))) throw Bad();
                length -= length % (ch * bits / 8);
                return (b.AsSpan(body, length).ToArray(), rate, ch, bits, tag == 3);
            }
            pos = body + size + (size & 1);
        }
        throw Bad();
    }

    void WriteHeader()
    {
        var h = new byte[44];
        void Put(int at, byte[] v) => v.CopyTo(h, at);
        Put(0, "RIFF"u8.ToArray()); Put(4, BitConverter.GetBytes((int)(36 + dataBytes))); Put(8, "WAVEfmt "u8.ToArray());
        Put(16, BitConverter.GetBytes(16)); Put(20, BitConverter.GetBytes((short)(isFloat ? 3 : 1))); Put(22, BitConverter.GetBytes((short)channels));
        Put(24, BitConverter.GetBytes(rate)); Put(28, BitConverter.GetBytes(rate * channels * bits / 8)); Put(32, BitConverter.GetBytes((short)(channels * bits / 8)));
        Put(34, BitConverter.GetBytes((short)bits)); Put(36, "data"u8.ToArray()); Put(40, BitConverter.GetBytes((int)dataBytes));
        var end = file.Position;
        file.Position = 0; file.Write(h); file.Position = Math.Max(end, 44);
    }

    void Feed(byte[] chunk)
    {
        file.Write(chunk); dataBytes += chunk.Length;
        var block = converter.Push(chunk);
        Deliver(block);
    }

    void Deliver(float[] block)
    {
        if (block.Length == 0) return;
        float peak = 0;
        foreach (var s in block) peak = Math.Max(peak, Math.Abs(s));
        lock (samples) samples.AddRange(block);
        onLevel(peak);
        onSamples(block);
    }

    void Run()
    {
        int frameBytes = channels * bits / 8, chunkFrames = Math.Max(1, (int)(rate * ChunkSeconds)), chunkBytes = chunkFrames * frameBytes;
        var clock = Stopwatch.StartNew();
        long sent = 0, offset = 0;
        while (!stopping)
        {
            // Real time: chunk n is due at n * ChunkSeconds.
            var due = TimeSpan.FromSeconds(sent * ChunkSeconds) - clock.Elapsed;
            if (due > TimeSpan.Zero) { Thread.Sleep(due); if (stopping) break; }
            var chunk = new byte[chunkBytes];
            if (offset < pcm.Length) { var n = (int)Math.Min(chunkBytes, pcm.Length - offset); Array.Copy(pcm, offset, chunk, 0, n); offset += n; }
            Feed(chunk);
            sent++;
        }
    }

    public AudioCaptureResult Stop()
    {
        if (!stopping)
        {
            stopping = true;
            thread.Join(2000);
            Deliver(converter.Flush());
            WriteHeader();
            file.Dispose();
        }
        lock (samples) return new AudioCaptureResult([.. samples], samples.Count / (double)AudioConverter.TargetRate, wavPath);
    }
}
