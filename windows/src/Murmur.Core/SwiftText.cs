// Swift String/Character semantics for the ports: grapheme clusters, Unicode predicates and case mapping.
// Predicates and case mappings read tables exported from the Mac's Swift (shared/cleanup_text.json "unicode"),
// not .NET's Unicode data, so they match the Mac exactly.
using System.Globalization;
using System.Text;

namespace Murmur.Core;

public static class SwiftText
{
    sealed class Tables
    {
        public Dictionary<string, string> Unicode { get; set; } = new();
    }

    static readonly (int Start, int End)[] Letter, Number, Space, Cased;
    static readonly Dictionary<int, string> Lower, Upper, Decomposition;
    static readonly (int Start, int End, int Class)[] Classes;

    static SwiftText()
    {
        var u = SharedData.Load<Tables>("cleanup_text").Unicode;
        Letter = Ranges(u["letter"]);
        Number = Ranges(u["number"]);
        Space = Ranges(u["whitespace"]);
        Cased = Ranges(u["cased"]);
        Lower = Mapping(u["lower"]);
        Upper = Mapping(u["upper"]);
        Decomposition = Mapping(u["decomposition"]);
        Classes = u["combiningClass"].Split(',').Select(e => e.Split(':')).Select(p => { var r = p[0].Split('-'); return (Convert.ToInt32(r[0], 16), Convert.ToInt32(r[1], 16), int.Parse(p[1])); }).ToArray();
    }

    static (int, int)[] Ranges(string s) => s.Split(',').Select(r => { var p = r.Split('-'); return (Convert.ToInt32(p[0], 16), Convert.ToInt32(p[1], 16)); }).ToArray();

    static Dictionary<int, string> Mapping(string s) => s.Split(',').Select(e => e.Split(':')).ToDictionary(
        p => Convert.ToInt32(p[0], 16), p => string.Concat(p[1].Split('+').Select(h => char.ConvertFromUtf32(Convert.ToInt32(h, 16)))));

    static bool InRanges((int Start, int End)[] ranges, int v)
    {
        int lo = 0, hi = ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (v < ranges[mid].Start) hi = mid - 1;
            else if (v > ranges[mid].End) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>The characters (extended grapheme clusters) of <paramref name="s"/>, as Swift's <c>Array(s)</c>.</summary>
    public static List<string> Characters(string s)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }

    /// <summary>Swift <c>s.count</c>.</summary>
    public static int Count(string s) => Characters(s).Count;

    /// <summary>Swift <c>String(s.prefix(n))</c>.</summary>
    public static string Prefix(string s, int n) => n <= 0 ? "" : string.Concat(Characters(s).Take(n));

    /// <summary>Swift <c>String(s.dropFirst())</c>.</summary>
    public static string DropFirst(string s) => string.Concat(Characters(s).Skip(1));

    static int FirstScalar(string c) => c.Length == 0 ? -1 : char.ConvertToUtf32(c, 0);

    // Swift Character properties: the first scalar decides (isLetter, isNumber, isWhitespace); case uses the mappings.
    public static bool IsLetter(string c) => InRanges(Letter, FirstScalar(c));
    public static bool IsNumber(string c) => InRanges(Number, FirstScalar(c));
    public static bool IsWhitespace(string c) => InRanges(Space, FirstScalar(c));
    // Observed on the Mac: a cased character is uppercase when uppercasing leaves it unchanged, lowercase when lowercasing does
    // (so "ª" and mathematical letters are both, and titlecase "ǅ" is neither).
    public static bool IsUppercase(string c) => InRanges(Cased, FirstScalar(c)) && c == Uppercased(c);
    public static bool IsLowercase(string c) => InRanges(Cased, FirstScalar(c)) && c == Lowercased(c);

    static string Map(string s, Dictionary<int, string> map)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
            if (map.TryGetValue(r.Value, out var m)) sb.Append(m); else sb.Append(r.ToString());
        return sb.ToString();
    }

    /// <summary>Swift <c>lowercased()</c>: scalar-by-scalar Unicode mapping, no locale or context.</summary>
    public static string Lowercased(string s) => Map(s, Lower);

    /// <summary>Swift <c>uppercased()</c> (full mapping: "ß" becomes "SS").</summary>
    public static string Uppercased(string s) => Map(s, Upper);

    static int CombiningClass(int v)
    {
        int lo = 0, hi = Classes.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (v < Classes[mid].Start) hi = mid - 1;
            else if (v > Classes[mid].End) lo = mid + 1;
            else return Classes[mid].Class;
        }
        return 0;
    }

    /// <summary>Canonical decomposition (NFD) as scalar values. Written here because .NET's <c>string.Normalize</c> does nothing for non-ASCII under InvariantGlobalization.</summary>
    public static List<int> CanonicalDecomposition(string s)
    {
        var o = new List<int>();
        foreach (var r in s.EnumerateRunes())
        {
            int v = r.Value;
            if (v is >= 0xAC00 and <= 0xD7A3)
            {
                int n = v - 0xAC00;
                o.Add(0x1100 + n / 588); o.Add(0x1161 + n % 588 / 28);
                if (n % 28 != 0) o.Add(0x11A7 + n % 28);
            }
            else if (Decomposition.TryGetValue(v, out var d)) foreach (var x in d.EnumerateRunes()) o.Add(x.Value);
            else o.Add(v);
        }
        // Canonical ordering: a stable sort of each run of non-starters by combining class.
        for (int i = 0; i < o.Count;)
        {
            if (CombiningClass(o[i]) == 0) { i++; continue; }
            int j = i;
            while (j < o.Count && CombiningClass(o[j]) != 0) j++;
            var run = o.GetRange(i, j - i).OrderBy(CombiningClass).ToList(); // OrderBy is stable.
            o.RemoveRange(i, j - i); o.InsertRange(i, run);
            i = j;
        }
        return o;
    }

    /// <summary>Swift <c>Character ==</c>: canonical equivalence ("é" equals "e" + U+0301).</summary>
    public static bool CharEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal) || CanonicalDecomposition(a).SequenceEqual(CanonicalDecomposition(b));

    /// <summary>Foundation <c>trimmingCharacters(in: .whitespacesAndNewlines)</c>.</summary>
    public static string TrimWhitespaceAndNewlines(string s)
    {
        static bool Ws(Rune r) => r.Value is 0x85 or 0x2028 or 0x2029 or 0x9 || (r.Value is >= 0xA and <= 0xD) || Rune.GetUnicodeCategory(r) == UnicodeCategory.SpaceSeparator;
        var runes = s.EnumerateRunes().ToList();
        int a = 0, b = runes.Count;
        while (a < b && Ws(runes[a])) a++;
        while (b > a && Ws(runes[b - 1])) b--;
        return string.Concat(runes.Skip(a).Take(b - a).Select(r => r.ToString()));
    }
}
