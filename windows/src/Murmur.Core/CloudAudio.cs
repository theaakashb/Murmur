// Port of app/CloudAudio.swift.
namespace Murmur.Core;

// Audio preparation for cloud upload: the WAV codec (16 kHz mono 16-bit PCM conversion) and peak measurement.

/// <summary>Converts the recorder's WAV (any channel count, 16/24/32-bit integer or float samples) to 16 kHz mono 16-bit PCM before
/// upload, and measures its peak for the silence pre-check. This keeps a five-minute dictation (the recording limit) near 9.6 MB,
/// under the providers' 20–25 MB limits. A file it cannot parse is sent unchanged, with no peak (the silence pre-check is then skipped).</summary>
public static class CloudAudio
{
    public sealed record Prepared(AudioPayload Payload, float? Peak, double? Duration);

    public static Prepared Prepare(byte[] data)
    {
        if (WAVAudio.Decode(data) is not { SampleRate: > 0 } decoded) return new Prepared(new AudioPayload(data), null, null);
        var rate = decoded.SampleRate > AudioFormat.SampleRate ? AudioFormat.SampleRate : decoded.SampleRate;
        var samples = TrimTrailingSilence(WAVAudio.Downsample(decoded.Samples, decoded.SampleRate, AudioFormat.SampleRate), rate);
        var payload = new AudioPayload(WAVAudio.EncodePCM16(samples, rate)) { Levels = WindowPeaks(samples, rate) };
        return new Prepared(payload, Math.Min(Level(decoded.Samples, decoded.SampleRate), 1), (double)decoded.Samples.Length / decoded.SampleRate);
    }

    /// <summary>The second-highest 100 ms window peak: speech fills several windows, while a lone click or key bump during a
    /// silent hold fills one and would otherwise decide the whole pre-check.
    /// shortcut: typing or repeated clicks during a silent hold still pass; a per-window energy measure needs calibrating against a real voice.</summary>
    public static float Level(float[] samples, int sampleRate)
    {
        var peaks = WindowPeaks(samples, sampleRate).OrderDescending().ToArray();
        return peaks.Length > 1 ? peaks[1] : peaks.Length == 1 ? peaks[0] : 0;
    }

    /// <summary>The peak of each 100 ms window, in order. Non-finite samples count as 0.</summary>
    public static float[] WindowPeaks(float[] samples, int sampleRate)
    {
        var window = Math.Max(sampleRate / 10, 1);
        var peaks = new float[(samples.Length + window - 1) / window];
        for (var w = 0; w < peaks.Length; w++)
        {
            float peak = 0;
            for (int i = w * window, end = Math.Min(i + window, samples.Length); i < end; i++)
                peak = Math.Max(peak, float.IsFinite(samples[i]) ? Math.Abs(samples[i]) : 0);
            peaks[w] = peak;
        }
        return peaks;
    }

    /// <summary>Drops the silent tail (below the silence pre-check's level) after the last speech, keeping <paramref name="pad"/> seconds,
    /// because Whisper-style models transcribe a silent tail as "Thank you." An all-silent clip is returned unchanged for the pre-check
    /// to judge. A click in the tail keeps the tail from there on, which costs only the trim, never speech.</summary>
    public static float[] TrimTrailingSilence(float[] samples, int sampleRate, double pad = 0.5)
    {
        var last = Array.FindLastIndex(samples, s => Math.Abs(s) >= AudioFormat.SilencePeak);
        if (last < 0) return samples;
        return samples[..(int)Math.Min(samples.Length, last + 1 + (long)(pad * sampleRate))];
    }
}

/// <summary>The WAV codec. Replaces AVFoundation reading on the Mac side of capture: RIFF/WAVE integer PCM (16/24/32-bit) or IEEE float (32/64-bit).</summary>
public static class WAVAudio
{
    public sealed record Decoded(float[] Samples, int SampleRate);

    static int U16(byte[] b, long i) => b[i] | b[i + 1] << 8;
    static long U32(byte[] b, long i) => (uint)(U16(b, i) | U16(b, i + 2) << 16);

    /// <summary>Mono samples in -1...1 (channels averaged). Null for anything but RIFF/WAVE PCM or float.</summary>
    public static Decoded? Decode(byte[] b)
    {
        if (b.Length < 12 || b[0] != 0x52 || b[1] != 0x49 || b[2] != 0x46 || b[3] != 0x46 || b[8] != 0x57 || b[9] != 0x41 || b[10] != 0x56 || b[11] != 0x45) return null;
        int format = 0, channels = 0, bits = 0;
        long rate = 0;
        (long Start, long End)? body = null;
        long i = 12;
        while (i + 8 <= b.Length)
        {
            var id = System.Text.Encoding.UTF8.GetString(b, (int)i, 4);
            var size = U32(b, i + 4);
            var start = i + 8;
            // A data chunk whose size was never finalized (0 or 0xFFFFFFFF) runs to the end of the file.
            if (id == "data" && (size == 0 || size == 0xFFFF_FFFF)) size = b.Length - start;
            var end = Math.Min(b.Length, start + size);
            if (id == "fmt " && end - start >= 16)
            {
                format = U16(b, start); channels = U16(b, start + 2); rate = U32(b, start + 4); bits = U16(b, start + 14);
                if (format == 0xFFFE && end - start >= 26) format = U16(b, start + 24);
            }
            else if (id == "data")
            {
                body = (start, end);
                break;
            }
            i = start + size + (size & 1);
        }
        if (body is not var (lo, hi) || channels <= 0 || channels > 32 || rate <= 0 || rate > int.MaxValue
            || !((format == 1 && bits is 16 or 24 or 32) || (format == 3 && bits is 32 or 64))) return null;
        var width = bits / 8;
        var frame = width * channels;
        var frames = (int)((hi - lo) / frame);
        var samples = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = lo + (long)f * frame + c * width;
                float value = (format, bits) switch
                {
                    (1, 16) => (short)U16(b, o) / 32768f,
                    (1, 24) => ((int)((uint)(U16(b, o) | b[o + 2] << 16) << 8) >> 8) / 8_388_608f,
                    (1, 32) => (int)(uint)U32(b, o) / 2_147_483_648f,
                    (3, 32) => BitConverter.UInt32BitsToSingle((uint)U32(b, o)),
                    _ => (float)BitConverter.UInt64BitsToDouble((ulong)U32(b, o) | (ulong)U32(b, o + 4) << 32),
                };
                sum += float.IsFinite(value) ? value : 0;
            }
            samples[f] = sum / channels;
        }
        return new Decoded(samples, (int)rate);
    }

    /// <summary>Box-filter decimation. Rates at or below <paramref name="to"/> are returned unchanged (never upsampled).</summary>
    public static float[] Downsample(float[] samples, int from, int to)
    {
        if (from <= to || to <= 0 || samples.Length == 0) return samples;
        var ratio = (double)from / to;
        var count = (int)(samples.Length / ratio);
        var output = new float[count];
        for (var n = 0; n < count; n++)
        {
            int lo = (int)(n * ratio), hi = Math.Min(samples.Length, Math.Max(lo + 1, (int)((n + 1) * ratio)));
            float sum = 0;
            for (var k = lo; k < hi; k++) sum += samples[k];
            output[n] = sum / (hi - lo);
        }
        return output;
    }

    public static byte[] EncodePCM16(float[] samples, int sampleRate)
    {
        var output = new byte[44 + samples.Length * 2];
        var p = 0;
        void Put32(int v) { for (var s = 0; s < 32; s += 8) output[p++] = (byte)((v >> s) & 0xFF); }
        void Put16(int v) { output[p++] = (byte)(v & 0xFF); output[p++] = (byte)((v >> 8) & 0xFF); }
        void Ascii(string s) { foreach (var c in s) output[p++] = (byte)c; }
        var bytes = samples.Length * 2;
        Ascii("RIFF"); Put32(36 + bytes); Ascii("WAVE");
        Ascii("fmt "); Put32(16); Put16(1); Put16(1); Put32(sampleRate); Put32(sampleRate * 2); Put16(2); Put16(16);
        Ascii("data"); Put32(bytes);
        foreach (var s in samples) Put16((ushort)(short)Math.Max(-32768f, Math.Min(32767f, (float.IsFinite(s) ? s : 0) * 32767)));
        return output;
    }
}
