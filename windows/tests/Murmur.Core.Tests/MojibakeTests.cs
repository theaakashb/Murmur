using System.Text.RegularExpressions;

namespace Murmur.Core.Tests;

/// <summary>UTF-8 text re-read as Windows-1252 turns curly quotes and dashes into "a-circumflex, euro" and "A-tilde, symbol" runs
/// (damage repaired in ad91b5c). Scans the sources so it cannot return. The patterns are written as escapes so this file stays clean.</summary>
public class MojibakeTests
{
    // Bytes 0x80-0xBF as Windows-1252 shows them: the second character of a re-read multi-byte UTF-8 sequence.
    const string Cont = "\u0080-\u00bf\u0152\u0153\u0160\u0161\u0178\u017d\u017e\u0192\u02c6\u02dc\u2013\u2014\u2018-\u201a\u201c-\u201e\u2020-\u2022\u2026\u2030\u2039\u203a\u20ac\u2122";

    // A lead byte (A-circumflex, A-tilde, a-circumflex) followed by a continuation, or any C1 control: Windows-1252 leaves 0x81, 0x8D,
    // 0x8F, 0x90 and 0x9D undefined, so the left arrow came back as a-circumflex, dagger, U+0090 (missed by the first pattern).
    static readonly Regex Damage = new($"[\u00c2\u00c3\u00e2][{Cont}]|[\u0080-\u009f]", RegexOptions.CultureInvariant);

    [Fact]
    public void SourcesAndDocsHaveNoDoubleEncodedText()
    {
        var src = Path.Combine(ModelTestSupport.RepoRoot(), "windows", "src");
        var sep = Path.DirectorySeparatorChar;
        var files = new[] { "*.cs", "*.md", "*.xaml" }.SelectMany(p => Directory.EnumerateFiles(src, p, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}")).ToList();
        Assert.NotEmpty(files);
        var bad = files.SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)).Where(x => Damage.IsMatch(x.line)))
            .Select(x => $"{Path.GetRelativePath(src, x.f)}:{x.i + 1}").ToList();
        Assert.True(bad.Count == 0, "Double-encoded text (mojibake) at: " + string.Join(", ", bad));
    }
}
