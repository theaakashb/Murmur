using System.Text.RegularExpressions;

namespace Murmur.Core.Tests;

/// <summary>UTF-8 text re-read as Windows-1252 turns curly quotes and dashes into "a-circumflex, euro" and "A-tilde, symbol" runs
/// (damage repaired in ad91b5c). Scans the sources so it cannot return. The patterns are written as escapes so this file stays clean.</summary>
public class MojibakeTests
{
    static readonly Regex Damage = new("\u00e2\u20ac|\u00c3[\u0080-\u00bf]", RegexOptions.CultureInvariant);

    [Fact]
    public void SourcesAndDocsHaveNoDoubleEncodedText()
    {
        var src = Path.Combine(ModelTestSupport.RepoRoot(), "windows", "src");
        var sep = Path.DirectorySeparatorChar;
        var files = new[] { "*.cs", "*.md" }.SelectMany(p => Directory.EnumerateFiles(src, p, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}")).ToList();
        Assert.NotEmpty(files);
        var bad = files.SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)).Where(x => Damage.IsMatch(x.line)))
            .Select(x => $"{Path.GetRelativePath(src, x.f)}:{x.i + 1}").ToList();
        Assert.True(bad.Count == 0, "Double-encoded text (mojibake) at: " + string.Join(", ", bad));
    }
}
