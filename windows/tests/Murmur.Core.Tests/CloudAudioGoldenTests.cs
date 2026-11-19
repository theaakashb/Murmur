// Compares Murmur.Core with golden/cloud_audio.json, which windows/golden/golden_cloud_audio.swift writes from the Mac's Swift.
using System.Text;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class CloudAudioGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("cloud_audio");
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];
    public static IEnumerable<object[]> Functions => GoldenFiles.Cases(Root, "functions", c => c.TryGetProperty("name", out var n) ? $"{n.GetString()} {c.GetProperty("rate")}" : "special");
    public static IEnumerable<object[]> Files => GoldenFiles.Cases(Root, "files", c => c.TryGetProperty("name", out var n)
        ? $"{n.GetString()} f{c.GetProperty("format")} b{c.GetProperty("bits")} c{c.GetProperty("channels")} r{c.GetProperty("rate")}" : "raw");

    /// <summary>The generator's integer recipes (same LCG, same float operations).</summary>
    internal static float[] Samples(JsonElement recipe)
    {
        var output = new List<float>();
        uint x = 12345;
        foreach (var seg in recipe.EnumerateArray())
        {
            var kind = seg[0].GetString();
            int count = seg[1].GetInt32();
            var amp = (float)seg[2].GetDouble();
            for (var i = 0; i < count; i++)
            {
                switch (kind)
                {
                    case "lcg":
                        x = unchecked(x * 1_103_515_245 + 12345);
                        output.Add((int)((x >> 8) & 0xFFFF) - 32768 is var v ? v / 32768f * amp : 0);
                        break;
                    case "square": output.Add((i / 20) % 2 == 0 ? amp : -amp); break;
                    case "ramp": output.Add((i % 200 - 100) / 100f * amp); break;
                    case "spike": output.Add(i == count / 2 ? amp : 0); break;
                    default: output.Add(amp); break;
                }
            }
        }
        return [.. output];
    }

    static byte[] Wav(float[] s, int rate, int channels, int format, int bits, bool extensible, bool extra, long? dataSize, int truncate)
    {
        var b = new List<byte>();
        void P32(long v) { for (var k = 0; k < 32; k += 8) b.Add((byte)((v >> k) & 0xFF)); }
        void P16(int v) { b.Add((byte)(v & 0xFF)); b.Add((byte)((v >> 8) & 0xFF)); }
        void Ascii(string t) => b.AddRange(Encoding.ASCII.GetBytes(t));
        var width = bits / 8;
        Ascii("RIFF"); P32(0); Ascii("WAVE");
        if (extra) { Ascii("LIST"); P32(5); b.AddRange([1, 2, 3, 4, 5, 0]); }
        Ascii("fmt "); P32(extensible ? 40 : 16); P16(extensible ? 0xFFFE : format); P16(channels); P32(rate); P32((long)rate * channels * width); P16(channels * width); P16(bits);
        if (extensible) { P16(22); P16(bits); P32(3); P16(format); b.AddRange([0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71]); }
        if (extra) { Ascii("FLLR"); P32(4); b.AddRange([0, 0, 0, 0]); }
        Ascii("data"); P32(dataSize ?? (long)s.Length * channels * width);
        foreach (var v in s)
        {
            for (var c = 0; c < channels; c++)
            {
                var x = v * (c + 1) / channels;
                switch ((format, bits))
                {
                    case (1, 16): P16((ushort)(short)Math.Max(-32768f, Math.Min(32767f, x * 32767))); break;
                    case (1, 24): var i24 = (int)Math.Max(-8_388_608f, Math.Min(8_388_607f, x * 8_388_607)); foreach (var k in new[] { 0, 8, 16 }) b.Add((byte)((i24 >> k) & 0xFF)); break;
                    case (1, 32): P32((uint)(int)Math.Max(-2_147_483_648.0, Math.Min(2_147_483_647.0, (double)x * 2_147_483_647))); break;
                    case (3, 32): P32(BitConverter.SingleToUInt32Bits(x)); break;
                    case (3, 64): var u = BitConverter.DoubleToUInt64Bits(x); P32((long)(u & 0xFFFF_FFFF)); P32((long)(u >> 32)); break;
                    default: for (var k = 0; k < width; k++) b.Add(0x7F); break;
                }
            }
        }
        var riff = b.Count - 8;
        for (var k = 0; k < 4; k++) b[4 + k] = (byte)((riff >> (8 * k)) & 0xFF);
        return [.. b.Take(Math.Max(0, b.Count - truncate))];
    }

    static string FloatSha(float[] s)
    {
        var bytes = new byte[s.Length * 4];
        for (var i = 0; i < s.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), BitConverter.SingleToUInt32Bits(s[i]));
        return ProviderGolden.Sha(bytes);
    }

    static int Bits(float f) => (int)BitConverter.SingleToUInt32Bits(f);
    static int[] Ints(JsonElement a) => [.. a.EnumerateArray().Select(x => (int)x.GetInt64())];
    static float[] FromBits(JsonElement a) => [.. a.EnumerateArray().Select(x => BitConverter.UInt32BitsToSingle((uint)x.GetInt64()))];

    [Theory, MemberData(nameof(Functions))]
    public void FunctionsMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("functions", i);
        if (c.TryGetProperty("special", out var special))
        {
            var odd = FromBits(special);
            Assert.Equal(c.GetProperty("pcm16Sha").GetString(), ProviderGolden.Sha(WAVAudio.EncodePCM16(odd, 16_000)));
            Assert.Equal(Ints(c.GetProperty("peaks")), CloudAudio.WindowPeaks(odd, 10).Select(Bits));
            Assert.Equal(c.GetProperty("trimmed").GetInt32(), CloudAudio.TrimTrailingSilence(odd, 4).Length);
            return;
        }
        var s = Samples(c.GetProperty("recipe"));
        var rate = c.GetProperty("rate").GetInt32();
        Assert.Equal(c.GetProperty("count").GetInt32(), s.Length);
        Assert.Equal(c.GetProperty("samplesSha").GetString(), FloatSha(s));
        var down = WAVAudio.Downsample(s, rate, AudioFormat.SampleRate);
        Assert.Equal(c.GetProperty("downsampleCount").GetInt32(), down.Length);
        Assert.Equal(c.GetProperty("downsampleSha").GetString(), FloatSha(down));
        var peaks = CloudAudio.WindowPeaks(s, rate);
        Assert.Equal(c.GetProperty("peaksCount").GetInt32(), peaks.Length);
        Assert.Equal(Ints(c.GetProperty("peaksHead")), peaks.Take(12).Select(Bits));
        Assert.Equal(c.GetProperty("peaksSha").GetString(), FloatSha(peaks));
        Assert.Equal(c.GetProperty("level").GetInt64(), Bits(CloudAudio.Level(s, rate)));
        Assert.Equal(c.GetProperty("trimmed").GetInt32(), CloudAudio.TrimTrailingSilence(s, rate).Length);
        Assert.Equal(c.GetProperty("trimmedPad0").GetInt32(), CloudAudio.TrimTrailingSilence(s, rate, 0).Length);
        var pcm = WAVAudio.EncodePCM16(s, rate);
        Assert.Equal(c.GetProperty("pcm16Length").GetInt32(), pcm.Length);
        Assert.Equal(c.GetProperty("pcm16Sha").GetString(), ProviderGolden.Sha(pcm));
    }

    [Theory, MemberData(nameof(Files))]
    public void FilesMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("files", i);
        byte[] data;
        if (c.TryGetProperty("raw", out var raw)) data = Encoding.UTF8.GetBytes(raw.GetString()!);
        else
        {
            data = Wav(Samples(c.GetProperty("recipe")), c.GetProperty("rate").GetInt32(), c.GetProperty("channels").GetInt32(), c.GetProperty("format").GetInt32(),
                       c.GetProperty("bits").GetInt32(), c.GetProperty("extensible").GetBoolean(), c.GetProperty("extra").GetBoolean(),
                       ProviderGolden.IsNull(c, "dataSize") ? null : c.GetProperty("dataSize").GetInt64(), c.GetProperty("truncate").GetInt32());
            Assert.Equal(c.GetProperty("fileLength").GetInt32(), data.Length);
            Assert.Equal(c.GetProperty("fileSha").GetString(), ProviderGolden.Sha(data));
        }
        var decoded = WAVAudio.Decode(data);
        var expected = c.GetProperty("decoded");
        if (expected.ValueKind == JsonValueKind.Null) Assert.Null(decoded);
        else if (expected.ValueKind == JsonValueKind.Number) Assert.Equal(expected.GetInt32(), decoded!.Samples.Length);
        else
        {
            Assert.NotNull(decoded);
            Assert.Equal(expected.GetProperty("rate").GetInt32(), decoded.SampleRate);
            Assert.Equal(expected.GetProperty("count").GetInt32(), decoded.Samples.Length);
            Assert.Equal(expected.GetProperty("samplesSha").GetString(), FloatSha(decoded.Samples));
        }
        var prepared = CloudAudio.Prepare(data);
        var p = c.GetProperty("prepared");
        Assert.Equal(p.GetProperty("length").GetInt32(), prepared.Payload.Data.Length);
        Assert.Equal(ProviderGolden.IsNull(p, "peak") ? null : (int?)p.GetProperty("peak").GetInt64(), prepared.Peak is float peak ? Bits(peak) : null);
        if (!p.TryGetProperty("sha", out var sha)) return;
        Assert.Equal(sha.GetString(), ProviderGolden.Sha(prepared.Payload.Data));
        Assert.Equal(ProviderGolden.IsNull(p, "duration") ? null : p.GetProperty("duration").GetDouble(), prepared.Duration);
        Assert.Equal(ProviderGolden.Str(p, "levels"), prepared.Payload.Levels is { } levels ? FloatSha([.. levels]) : null);
        Assert.Equal(ProviderGolden.IsNull(p, "levelsCount") ? null : p.GetProperty("levelsCount").GetInt32(), prepared.Payload.Levels?.Count);
        Assert.Equal(ProviderGolden.Str(p, "mimeType"), prepared.Payload.MimeType);
        Assert.Equal(ProviderGolden.Str(p, "filename"), prepared.Payload.Filename);
        Assert.Equal(ProviderGolden.Str(p, "language"), prepared.Payload.Language);
    }
}
