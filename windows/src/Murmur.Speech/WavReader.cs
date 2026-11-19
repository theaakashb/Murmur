namespace Murmur.Speech;

/// <summary>Reads a RIFF/WAVE file (PCM 8/16/24/32-bit or 32-bit float, any channel count) as 16 kHz mono floats.</summary>
static class WavReader
{
    public const int Rate = 16000;

    public static float[] Read(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            throw new InvalidDataException("Invalid or oversized audio");
        int format = 0, channels = 0, rate = 0, bits = 0, dataAt = -1, dataLength = 0;
        for (int p = 12; p + 8 <= b.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(b, p, 4);
            long size = BitConverter.ToUInt32(b, p + 4);
            int body = p + 8;
            if (id == "fmt " && size >= 16 && body + 16 <= b.Length)
            {
                format = BitConverter.ToUInt16(b, body); channels = BitConverter.ToUInt16(b, body + 2);
                rate = (int)BitConverter.ToUInt32(b, body + 4); bits = BitConverter.ToUInt16(b, body + 14);
                if (format == 0xFFFE && size >= 26 && body + 26 <= b.Length) format = BitConverter.ToUInt16(b, body + 24);
            }
            else if (id == "data") { dataAt = body; dataLength = (int)Math.Min(size, (long)(b.Length - body)); break; }
            p = (int)Math.Min(int.MaxValue, body + size + (size & 1));
        }
        int bytes = bits / 8;
        bool pcm = format == 1 && bytes is 1 or 2 or 3 or 4, flt = format == 3 && bits == 32;
        if (dataAt < 0 || channels < 1 || rate < 1000 || !(pcm || flt)) throw new InvalidDataException("Invalid or oversized audio");
        int frames = dataLength / (bytes * channels);
        if (frames == 0) throw new InvalidDataException("Invalid or oversized audio");
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int o = dataAt + (i * channels + c) * bytes;
                sum += flt ? BitConverter.ToSingle(b, o) : bytes switch
                {
                    1 => (b[o] - 128) / 128.0,
                    2 => BitConverter.ToInt16(b, o) / 32768.0,
                    3 => ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608.0,
                    _ => BitConverter.ToInt32(b, o) / 2147483648.0,
                };
            }
            mono[i] = (float)(sum / channels);
        }
        return rate == Rate ? mono : Resample(mono, rate);
    }

    // shortcut: a box filter (mean over each output sample's span) rather than a windowed-sinc low-pass; the app resamples properly before the warm helper and only the one-shot fallback reads device-rate files.
    static float[] Resample(float[] input, int rate)
    {
        var output = new float[(int)((long)input.Length * Rate / rate)];
        double step = (double)rate / Rate;
        for (int i = 0; i < output.Length; i++)
        {
            double from = i * step, to = Math.Min(from + step, input.Length), sum = 0;
            for (int k = (int)from; k < to; k++) sum += input[k] * (Math.Min(k + 1, to) - Math.Max(k, from));
            output[i] = (float)(sum / (to - from));
        }
        return output;
    }
}
