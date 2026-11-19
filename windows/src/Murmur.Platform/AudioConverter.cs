namespace Murmur.Platform;

public enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

/// <summary>
/// Streaming conversion of interleaved device audio to 16 kHz mono Float32 (what the speech engines and the cloud upload take, spec/pipeline.md §3).
/// Channels are averaged, then resampled with a Hann-windowed sinc low-pass so 48 kHz does not alias into the band.
/// </summary>
public sealed class AudioConverter
{
    public const int TargetRate = 16000;
    const int ZeroCrossings = 12;

    readonly int channels, bytesPerSample;
    readonly SampleKind kind;
    readonly double step, cutoff, half;
    float[] buffer = new float[4096]; // mono input not yet fully consumed
    int count;
    long baseIndex;     // absolute input index of buffer[0]
    long totalIn, emitted;

    public AudioConverter(int inputRate, int channels, SampleKind kind)
    {
        if (inputRate <= 0 || channels <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        this.channels = channels;
        this.kind = kind;
        bytesPerSample = kind switch { SampleKind.Pcm16 => 2, SampleKind.Pcm24 => 3, _ => 4 };
        step = (double)inputRate / TargetRate;
        cutoff = 0.9 / Math.Max(1, step); // fraction of the input rate; 0.9 of the output Nyquist when downsampling
        half = ZeroCrossings * Math.Max(1, step);
    }

    /// <summary>Converts a block of interleaved frames in the device's sample format; returns the new 16 kHz mono samples.</summary>
    public float[] Push(ReadOnlySpan<byte> interleaved)
    {
        int frames = interleaved.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += Decode(interleaved.Slice((i * channels + c) * bytesPerSample, bytesPerSample));
            mono[i] = sum / channels;
        }
        return Resample(mono);
    }

    /// <summary>Same, for interleaved Float32 already decoded (synthetic sources).</summary>
    public float[] Push(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        return Resample(mono);
    }

    /// <summary>Emits the tail still held by the filter. Call once at the end of a recording.</summary>
    public float[] Flush()
    {
        var tail = Resample(new float[(int)half + 2], flushing: true);
        return tail;
    }

    float Decode(ReadOnlySpan<byte> b) => kind switch
    {
        SampleKind.Float32 => BitConverter.ToSingle(b),
        SampleKind.Pcm16 => BitConverter.ToInt16(b) / 32768f,
        SampleKind.Pcm24 => ((b[0] << 8 | b[1] << 16 | b[2] << 24) >> 8) / 8388608f,
        _ => BitConverter.ToInt32(b) / 2147483648f,
    };

    float[] Resample(float[] mono, bool flushing = false)
    {
        if (!flushing) totalIn += mono.Length;
        if (count + mono.Length > buffer.Length) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, count + mono.Length));
        mono.CopyTo(buffer, count);
        count += mono.Length;

        long limit = (long)Math.Floor(totalIn / step + 1e-9);
        var output = new List<float>();
        while (true)
        {
            double t = emitted * step;
            if (flushing ? emitted >= limit : t + half >= baseIndex + count) break;
            long first = Math.Max((long)Math.Ceiling(t - half), baseIndex), last = Math.Min((long)Math.Floor(t + half), baseIndex + count - 1);
            double acc = 0, weights = 0;
            for (long n = first; n <= last; n++)
            {
                double x = n - t, w = Sinc(x * cutoff) * (0.5 + 0.5 * Math.Cos(Math.PI * x / half));
                acc += w * buffer[n - baseIndex];
                weights += w;
            }
            output.Add(weights == 0 ? 0 : (float)(acc / weights)); // unit gain at DC, also at the start where taps fall before sample 0
            emitted++;
        }
        // drop input no later output can need
        long keepFrom = Math.Max(baseIndex, (long)Math.Ceiling(emitted * step - half));
        int drop = (int)(keepFrom - baseIndex);
        if (drop > 0 && drop <= count) { Array.Copy(buffer, drop, buffer, 0, count - drop); count -= drop; baseIndex = keepFrom; }
        return output.ToArray();
    }

    static double Sinc(double x) => Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
}
