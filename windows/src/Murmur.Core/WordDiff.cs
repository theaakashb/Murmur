// Port of app/WordDiff.swift.
namespace Murmur.Core;

public static class WordDiff
{
    public enum Kind { Same, Removed, Added, Tweaked }

    public sealed record Run(Kind Kind, string Text, IReadOnlyList<int> Marks)
    {
        public Run(Kind kind, string text) : this(kind, text, []) { }
    }

    static string Core(string word) => string.Concat(SwiftText.Characters(SwiftText.Lowercased(word))
        .Where(c => SwiftText.IsLetter(c) || SwiftText.IsNumber(c)));

    public static IReadOnlyList<Run> Runs(string raw, string final) =>
        Diff(Insights.SplitWords(raw), Insights.SplitWords(final), word => Core(word) is { Length: > 0 } core ? core : word);

    // The display diff pairs on canonical keys, but keeps the source spelling and the Swift removal-first tie break.
    static int[,] Alignment(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        return table;
    }

    static IReadOnlyList<int> ChangedChars(string old, string edited)
    {
        var a = SwiftText.Characters(old).Select(Strings.Key).ToArray();
        var b = SwiftText.Characters(edited).Select(Strings.Key).ToArray();
        var table = Alignment(a, b);
        var marks = new List<int>();
        int i = 0, j = 0;
        while (j < b.Length)
        {
            if (i < a.Length && a[i] == b[j]) { i++; j++; }
            else if (i < a.Length && table[i + 1, j] >= table[i, j + 1]) i++;
            else marks.Add(j++);
        }
        return marks;
    }

    static IReadOnlyList<Run> Diff(IReadOnlyList<string> raw, IReadOnlyList<string> final, Func<string, string> key)
    {
        var a = raw.Select(w => Strings.Key(key(w))).ToArray();
        var b = final.Select(w => Strings.Key(key(w))).ToArray();
        var table = Alignment(a, b);
        var result = new List<Run>();
        void Add(Kind kind, string word)
        {
            if (result.Count > 0 && result[^1].Kind == kind)
                result[^1] = new(kind, result[^1].Text + " " + word);
            else result.Add(new(kind, word));
        }
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            if (i < a.Length && j < b.Length && a[i] == b[j])
            {
                if (SwiftText.CharEquals(raw[i], final[j])) Add(Kind.Same, raw[i]);
                else result.Add(new(Kind.Tweaked, final[j], ChangedChars(raw[i], final[j])));
                i++;
                j++;
            }
            else if (i < a.Length && (j == b.Length || table[i + 1, j] >= table[i, j + 1])) Add(Kind.Removed, raw[i++]);
            else Add(Kind.Added, final[j++]);
        }
        return result;
    }

    public static int ChangeCount(string raw, string final)
    {
        static string[] Cores(string text) => Insights.SplitWords(text).Select(Core).Where(w => w.Length > 0).ToArray();
        int count = 0, pendingRemoved = 0;
        foreach (var run in Diff(Cores(raw), Cores(final), w => w))
        {
            switch (run.Kind)
            {
                case Kind.Removed: pendingRemoved = Insights.SplitWords(run.Text).Count; break;
                case Kind.Added: count += Math.Max(pendingRemoved, Insights.SplitWords(run.Text).Count); pendingRemoved = 0; break;
                default: if (pendingRemoved > 0) count++; pendingRemoved = 0; break;
            }
        }
        return count + (pendingRemoved > 0 ? 1 : 0);
    }

    public static string RevertText(string raw, string inserted) =>
        Strings.HasPrefix(inserted, " ") && !Strings.HasPrefix(raw, " ") ? " " + raw : raw;
}
