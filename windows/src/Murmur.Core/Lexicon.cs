using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Murmur.Core;

/// <summary>
/// The English lexicon behind every <c>isCommonWord</c> check (dictionary respelling, learning, BoostFilter).
/// The Mac asks NSSpellChecker; Windows ships a pinned word list instead (SCOWL 2026.02.25, size 60, American,
/// lowercase-only, no abbreviations; see windows/shared/lexicon/README.md), so every PC and CI agree.
/// It is thread-safe and immutable after the first load.
/// </summary>
public sealed class Lexicon
{
    const string Resource = "shared/lexicon/english-scowl-60.txt.gz";

    static readonly Lazy<Lexicon> english = new(LoadEnglish, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The bundled English lexicon, loaded on first use.</summary>
    public static Lexicon English => english.Value;

    readonly HashSet<string> words;

    public Lexicon(IEnumerable<string> lowercaseWords) => words = new HashSet<string>(lowercaseWords, StringComparer.Ordinal);

    public int Count => words.Count;

    /// <summary>
    /// True for an ordinary English word. Case-insensitive, so "Paris" and "paris" answer alike (the list
    /// holds only words SCOWL lists in lowercase, so names are not common). A typographic apostrophe counts as
    /// "'". "word's" is common when "word" is; a hyphenated word is common when every part is, which is how
    /// NSSpellChecker treated hyphenated words in the measured sample.
    /// </summary>
    public bool IsCommonWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        var w = word.Trim().Replace('’', '\'').ToLowerInvariant();
        if (Base(w)) return true;
        if (!w.Contains('-')) return false;
        foreach (var part in w.Split('-'))
            if (part.Length == 0 || !Base(part)) return false;
        return true;
    }

    bool Base(string w) => words.Contains(w) || (w.EndsWith("'s", StringComparison.Ordinal) && words.Contains(w[..^2]));

    static Lexicon LoadEnglish()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Missing embedded resource {Resource}.");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, new UTF8Encoding(false));
        var list = new List<string>(90_000);
        for (var line = reader.ReadLine(); line != null; line = reader.ReadLine())
            if (line.Length > 0) list.Add(line);
        return new Lexicon(list);
    }
}
