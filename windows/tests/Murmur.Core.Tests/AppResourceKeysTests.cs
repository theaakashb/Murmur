using System.Text.RegularExpressions;

namespace Murmur.Core.Tests;

/// <summary>A style or brush the app looks up by key but App.xaml no longer defines crashes WinUI at launch (COMException
/// 0x80004005, VM 2026-11-21, after the pill rewrite dropped MurmurBubble and MurmurBubbleDot). Checked from the sources, so it
/// runs on every CI host without starting the app.</summary>
public class AppResourceKeysTests
{
    static readonly string AppDir = Path.Combine(ModelTestSupport.RepoRoot(), "windows", "src", "Murmur.App");

    // MurmurStyle.Style("K"), MurmurStyle.ThemeBrush("K", …), MurmurStyle.Text(…, "K"), Resources["K"].
    static readonly Regex Lookup = new(@"(?:\bStyle\(|\bThemeBrush\(|Resources\[)\s*""(Murmur\w+)""|\bText\([^;]*?,\s*""(Murmur\w+)""\)", RegexOptions.CultureInvariant);

    [Fact]
    public void EveryKeyTheAppLooksUpIsDefinedInAppXaml()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDir, "App.xaml"));
        var defined = Regex.Matches(xaml, @"x:Key=""(\w+)""").Select(m => m.Groups[1].Value).ToHashSet();
        var used = Directory.EnumerateFiles(AppDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Lookup.Matches(File.ReadAllText(f)).Select(m => (Key: m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, File: Path.GetFileName(f))))
            .ToList();
        Assert.NotEmpty(used);
        var missing = used.Where(u => !defined.Contains(u.Key)).Select(u => $"{u.Key} ({u.File})").Distinct().ToList();
        Assert.True(missing.Count == 0, "App.xaml is missing: " + string.Join(", ", missing));
    }
}
