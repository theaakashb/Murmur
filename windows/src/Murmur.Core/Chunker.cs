// Port of the audio cuts in app/Capture.swift and the transcript joins in app/ModelHelper.swift.
using System.Text;

namespace Murmur.Core;

public sealed class Chunker(Action<Chunker.Chunk> onChunk, Action<float[]>? onBoundary = null)
{
    public const double Target = 15, Slack = 5, Window = 0.2, BoundarySpan = 3, SentencePause = 0.45;
    static readonly int Rate = AudioFormat.SampleRate;
    public sealed record Chunk(float[] Samples, bool SentenceEnd);
    readonly List<float[]> buffers = [];
    readonly List<(int Frames, double Energy)> shape = [];
    int frames;
    float[]? before;

    public void Append(float[] samples)
    {
        if (samples.Length == 0) return;
        buffers.Add(samples);
        double energy = 0;
        foreach (double sample in samples) energy += sample * sample;
        shape.Add((samples.Length, energy));
        frames += samples.Length;
        if (frames >= BoundarySpan * Rate) EmitBoundary();
        if (frames < (Target + Slack) * Rate) return;

        var cut = QuietestCut(shape, Rate);
        var sentenceEnd = PauseLength(shape, cut, Rate) >= SentencePause;
        var chunk = buffers.Take(cut).SelectMany(b => b).ToArray();
        onChunk(new Chunk(chunk, sentenceEnd));
        if (onBoundary is not null)
        {
            int length = 0;
            for (int i = cut - 1; i >= 0 && length < BoundarySpan * Rate; i--) length += buffers[i].Length;
            before = chunk[^length..];
        }
        buffers.RemoveRange(0, cut);
        shape.RemoveRange(0, cut);
        frames -= chunk.Length;
        if (frames >= BoundarySpan * Rate) EmitBoundary();
    }

    public void Finish()
    {
        EmitBoundary();
        if (frames > 0) onChunk(new Chunk(buffers.SelectMany(b => b).ToArray(), false));
        buffers.Clear();
        shape.Clear();
        frames = 0;
    }

    void EmitBoundary()
    {
        if (before is not { } tail) return;
        before = null;
        if (frames == 0) return;
        int count = 0, length = 0;
        while (count < buffers.Count && length < BoundarySpan * Rate) length += buffers[count++].Length;
        onBoundary!([.. tail, .. buffers.Take(count).SelectMany(b => b)]);
    }

    public static int QuietestCut(IReadOnlyList<(int Frames, double Energy)> buffers, double rate)
    {
        var starts = new int[buffers.Count + 1];
        for (int i = 0; i < buffers.Count; i++) starts[i + 1] = starts[i] + buffers[i].Frames;
        double lo = (Target - Slack) * rate, hi = (Target + Slack) * rate, span = Window * rate;
        int cut = buffers.Count, end = 0;
        double best = double.PositiveInfinity, energy = 0;
        for (int begin = 0; begin < buffers.Count; begin++)
        {
            while (end < buffers.Count && starts[end] - starts[begin] < span) energy += buffers[end++].Energy;
            if (starts[end] - starts[begin] < span) break;
            int middle = (begin + end) / 2;
            if (starts[middle] >= lo && starts[middle] <= hi && energy < best)
            {
                best = energy;
                cut = middle;
            }
            energy -= buffers[begin].Energy;
        }
        return Math.Max(1, cut);
    }

    public static double PauseLength(IReadOnlyList<(int Frames, double Energy)> buffers, int cut, double rate)
    {
        if (buffers.Count == 0) return 0;
        double quiet = buffers.Select(b => b.Energy).Order().ElementAt(buffers.Count / 2) * 0.01;
        int frames = 0;
        for (int i = cut - 1; i >= 0 && buffers[i].Energy <= quiet; i--) frames += buffers[i].Frames;
        for (int i = cut; i < buffers.Count && buffers[i].Energy <= quiet; i++) frames += buffers[i].Frames;
        return frames / rate;
    }

    static bool IsTermCharacter(string c) => SwiftText.IsLetter(c) || SwiftText.IsNumber(c);
    static string Letters(string text) => string.Concat(SwiftText.Characters(SwiftText.Lowercased(text)).Where(IsTermCharacter));

    public static (string Tail, string Head)? Repair(string tail, string head, string from, string to)
    {
        var target = Letters(from);
        if (target.Length == 0) return null;
        var left = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var right = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int t = 1; t <= Math.Min(3, left.Length); t++)
        {
            for (int h = 1; h <= Math.Min(3, right.Length); h++)
            {
                var spoken = string.Concat(left.TakeLast(t).Concat(right.Take(h)).Select(Letters));
                if (!SwiftText.CharEquals(spoken, target)) continue;
                var trailing = string.Concat(SwiftText.Characters(right[h - 1]).AsEnumerable().Reverse().TakeWhile(c => !IsTermCharacter(c)).Reverse());
                return (string.Join(' ', left.SkipLast(t).Append(to + trailing)), string.Join(' ', right.Skip(h)));
            }
        }
        return null;
    }

    public static string Join(IReadOnlyList<string> texts, IReadOnlyList<bool> sentenceEnds)
    {
        var output = new StringBuilder();
        bool sentenceBreak = false;
        for (int i = 0; i < texts.Count; i++)
        {
            var text = SwiftText.TrimWhitespaceAndNewlines(texts[i]);
            if (text.Length > 0)
            {
                if (output.Length > 0)
                {
                    var lastWord = output.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
                    var lastCharacters = SwiftText.Characters(SwiftText.Lowercased(lastWord));
                    static bool IsPunctuation(string c) => char.IsPunctuation(c, 0);
                    bool dangling = Words.DanglingWords.Contains(string.Concat(lastCharacters.SkipWhile(IsPunctuation).Reverse().SkipWhile(IsPunctuation).Reverse()));
                    char last = output[^1];
                    if (!sentenceBreak || dangling)
                    {
                        var first = SwiftText.Characters(text).TakeWhile(c => SwiftText.IsLetter(c) || c is "'" or "’").ToArray();
                        var word = string.Concat(first);
                        var lower = SwiftText.Lowercased(word);
                        bool period = last == '.' && (output.Length < 2 || output[^2] != '.');
                        if (period && dangling) output.Length--;
                        if ((!period || dangling) && first.Length > 0 && SwiftText.IsUppercase(first[0]) && Words.LowercaseStarts.Contains(lower))
                            text = lower + text[word.Length..];
                    }
                    else if (last is not ('.' or '?' or '!')) output.Append('.');
                    output.Append(' ');
                }
                output.Append(text);
            }
            sentenceBreak = i < sentenceEnds.Count && sentenceEnds[i];
        }
        return output.ToString();
    }

    sealed record JoinWords(HashSet<string> DanglingWords, HashSet<string> LowercaseStarts);
    static readonly JoinWords Words = SharedData.Load<JoinWords>("chunker_words");
}
