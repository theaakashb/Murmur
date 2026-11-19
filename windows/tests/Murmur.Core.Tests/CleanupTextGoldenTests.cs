// Compares Murmur.Core with golden/cleanup_text.json, which windows/golden/golden_cleanup_text.swift writes from the Mac's Swift.
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class CleanupTextGoldenTests
{
    static readonly JsonElement Root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "cleanup_text.json"))).RootElement;

    // The second argument only names the case in the test report.
    static IEnumerable<object[]> Cases(string section) => Root.GetProperty(section).EnumerateArray().Select((c, i) =>
    {
        var text = (c.TryGetProperty("input", out var inp) ? inp : c.TryGetProperty("a", out inp) ? inp : c.GetProperty("text")).GetString()!;
        return new object[] { i, Show(text) };
    });
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];

    public static IEnumerable<object[]> Text => Cases("text");
    public static IEnumerable<object[]> TokenProps => Cases("tokenProps");
    public static IEnumerable<object[]> Words => Cases("words");
    public static IEnumerable<object[]> Similarity => Cases("similarity");
    public static IEnumerable<object[]> Sanitized => Cases("sanitized");
    public static IEnumerable<object[]> Chars => Cases("chars");

    static string Bits(IEnumerable<bool> a) => string.Concat(a.Select(b => b ? "1" : "0"));
    static string Ints(IEnumerable<int?> a) => string.Join(",", a.Select(x => x ?? -1));
    static string Encode(CleanupToken t) => (t.Kind switch { TokenKind.Word => "w", TokenKind.Punct => "p", _ => "s" }) + "|" + t.Text;
    static string Show(string s) => (s.Length > 40 ? s[..40] + "..." : s).Replace("\n", "\\n").Replace("\r", "\\r");

    [Theory, MemberData(nameof(Text))]
    public void TokenizeAndHelpers(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("text", i);
        var input = c.GetProperty("input").GetString()!;
        var t = CleanupText.Tokenize(input);
        Assert.Equal(c.GetProperty("tokens").EnumerateArray().Select(x => x.GetString()), t.Select(Encode));
        Assert.Equal(c.GetProperty("rendered").GetString(), CleanupText.Render(t));
        Assert.Equal(c.GetProperty("normalized").GetString(), CleanupText.Render(CleanupText.Normalize(t)));
        Assert.Equal(c.GetProperty("joiningParagraphs").GetString(), CleanupText.JoiningParagraphs(input));
        Assert.Equal(c.GetProperty("quotedMask").GetString(), Bits(CleanupText.QuotedMask(t)));
        var idx = Enumerable.Range(0, t.Count).ToList();
        Assert.Equal(c.GetProperty("sentenceStart").GetString(), Bits(idx.Select(k => CleanupText.IsSentenceStart(t, k))));
        Assert.Equal(c.GetProperty("prevNonSpace").GetString(), Ints(idx.Select(k => CleanupText.PrevNonSpace(t, k))));
        Assert.Equal(c.GetProperty("nextNonSpace").GetString(), Ints(idx.Select(k => CleanupText.NextNonSpace(t, k))));
        Assert.Equal(c.GetProperty("nextWord").GetString(), Ints(idx.Select(k => CleanupText.NextWord(t, k))));
        foreach (var tone in Enum.GetValues<Tone>())
        {
            var name = JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(tone, SharedData.JsonOptions))!;
            Assert.Equal(c.GetProperty("tone").GetProperty(name).GetString(), tone.Apply(input));
        }
    }

    [Theory, MemberData(nameof(TokenProps))]
    public void TokenProperties(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("tokenProps", i);
        var text = c.GetProperty("text").GetString()!;
        var kind = c.GetProperty("kind").GetString() switch { "word" => TokenKind.Word, "punct" => TokenKind.Punct, _ => TokenKind.Space };
        var t = new CleanupToken(kind, text);
        Assert.Equal(c.GetProperty("lower").GetString(), t.Lower);
        Assert.Equal(c.GetProperty("isTerminal").GetBoolean(), t.IsTerminal);
        Assert.Equal(c.GetProperty("isDash").GetBoolean(), t.IsDash);
        Assert.Equal(c.GetProperty("isComma").GetBoolean(), t.IsComma);
        Assert.Equal(c.GetProperty("isBoundary").GetBoolean(), t.IsBoundary);
    }

    [Theory, MemberData(nameof(Words))]
    public void WordFunctions(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("words", i);
        var w = c.GetProperty("input").GetString()!;
        Assert.Equal(c.TryGetProperty("numberValue", out var nv) ? nv.GetString() : null, CleanupText.NumberValue(w)); // Swift omits nil keys.
        Assert.Equal(c.GetProperty("isNegation").GetBoolean(), CleanupText.IsNegation(w));
        Assert.Equal(c.GetProperty("capitalized").GetString(), CleanupText.Capitalized(w));
        Assert.Equal(c.GetProperty("isWordChar").EnumerateArray().Select(x => x.GetBoolean()), SwiftText.Characters(w).Select(CleanupText.IsWordChar));
    }

    [Theory, MemberData(nameof(Similarity))]
    public void SimilarityMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("similarity", i);
        Assert.Equal(c.GetProperty("value").GetDouble(), CleanupText.Similarity(c.GetProperty("a").GetString()!, c.GetProperty("b").GetString()!));
    }

    [Theory, MemberData(nameof(Sanitized))]
    public void ScreenContextSanitized(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("sanitized", i);
        Assert.Equal(c.GetProperty("value").GetString(), ScreenContext.Sanitized(c.GetProperty("input").GetString(), c.GetProperty("limit").GetInt32()));
    }

    [Theory, MemberData(nameof(Chars))]
    public void CharacterPredicates(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("chars", i);
        var s = c.GetProperty("input").GetString()!;
        Assert.Equal(c.GetProperty("isLetter").GetBoolean(), SwiftText.IsLetter(s));
        Assert.Equal(c.GetProperty("isNumber").GetBoolean(), SwiftText.IsNumber(s));
        Assert.Equal(c.GetProperty("isWhitespace").GetBoolean(), SwiftText.IsWhitespace(s));
        Assert.Equal(c.GetProperty("isUppercase").GetBoolean(), SwiftText.IsUppercase(s));
        Assert.Equal(c.GetProperty("isLowercase").GetBoolean(), SwiftText.IsLowercase(s));
        Assert.Equal(c.GetProperty("lowercased").GetString(), SwiftText.Lowercased(s));
        Assert.Equal(c.GetProperty("uppercased").GetString(), SwiftText.Uppercased(s));
    }
}
