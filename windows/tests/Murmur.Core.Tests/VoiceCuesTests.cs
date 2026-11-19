// Ports app/check_voice_cues.swift: matching, joining and the store.
using System.Text;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class VoiceCuesTests
{
    const string Url = "https://example.com/in/sample?a=b&c=d", Email = "sam@example.com", Address = "1 Sample St\nTown, ST 00000", Work = "sam@work.example.com";
    static VoiceCue Cue(string phrase, string text) => new() { Id = Guid.NewGuid(), Phrase = phrase, Text = text, CreatedAt = DateTime.UnixEpoch };
    static readonly VoiceCue[] Library = [Cue("my LinkedIn", Url), Cue("my email", Email), Cue("my email address", Work), Cue("my address", Address), Cue("Sam's phone", "555-0100"), Cue("my signature", "Thanks,\nSam\n")];

    /// <summary>What ordinary cleanup does to a stretch of words: a capital and a closing period.</summary>
    static string Clean(string text)
    {
        var chars = SwiftText.Characters(text);
        if (chars.Count == 0) return text;
        var sentence = SwiftText.Uppercased(chars[0]) + string.Concat(chars.Skip(1));
        return sentence[^1] is '.' or '!' or '?' ? sentence : sentence + ".";
    }

    static string? Run(string raw, IEnumerable<VoiceCue>? cues = null, Func<string, bool>? keepsCase = null)
    {
        var pieces = VoiceCueText.Split(raw, cues ?? Library);
        if (pieces is null) return null;
        var processed = pieces.Select(p => p is VoiceCuePiece.Text t ? Clean(t.Raw) : "").ToList();
        return VoiceCueText.Join(pieces, processed, keepsCase);
    }

    static VoiceCuePiece Expansion(string text, string closing = "") => new VoiceCuePiece.Expansion(text, closing);

    [Fact]
    public void Matching()
    {
        var c = new Checker();
        // Rule 1: the whole dictation is the phrase.
        foreach (var raw in new[] { "my LinkedIn", "  My LinkedIn! ", "MY LINKEDIN.", "my, linkedin?", "\"my LinkedIn\"", "insert my LinkedIn", "Paste my LinkedIn here." })
        {
            var split = VoiceCueText.Split(raw, Library);
            c.Check(split is not null && split.SequenceEqual([Expansion(Url)]) || split is not null && split.SequenceEqual([Expansion(Url, ".")]), $"whole phrase: {raw}");
        }
        c.Check(Run("my LinkedIn") == Url && Run("My email address.") == Work, "whole phrase gives the cue text alone");
        // Rule 2: commands anywhere, with or without "here".
        c.Check(Run("Reach me at insert my LinkedIn here if you have questions") == $"Reach me at {Url} if you have questions.", "inline command with here");
        c.Check(Run("Reach me at paste my email if you like") == $"Reach me at {Email} if you like.", "paste, no here");
        c.Check(Run("INSERT MY LINKEDIN HERE") == Url, "case ignored");
        c.Check(Run("insert my LinkedIn, here") == Url && Run("insert, my LinkedIn here") == Url, "punctuation inside the command");
        c.Check(Run("Insert my email here.") == Email + ".", "closing period stays after the expansion");
        c.Check(Run("insert my email here and insert my LinkedIn here") == $"{Email} and {Url}", "two commands");
        c.Check(Run("insert my email here insert my email here") == $"{Email} {Email}", "adjacent commands");
        // Rule 3: a bare phrase or a different lead word does not fire.
        foreach (var raw in new[] { "my LinkedIn is down", "send my email address to Sam", "please insert the table here", "please update my LinkedIn", "inserting my LinkedIn", "insert my",
                                     "insert. my LinkedIn", "I want to add my LinkedIn", "insert here", "my link" })
            c.Check(Run(raw) is null, $"does not fire: {raw}");
        // Rule 4: apostrophes, possessives, curly quotes; the longest phrase wins.
        foreach (var raw in new[] { "insert Sam's phone here", "insert Sams phone here", "insert Sam’s phone here", "Sam's phone" })
            c.Check(Run(raw) == "555-0100", $"apostrophe variant: {raw}");
        c.Check(Run("insert my email address here") == Work && Run("insert my email here") == Email, "longest phrase wins");
        c.Check(Run("insert my email addresses here") == $"{Email} addresses here.", "a longer word is not the phrase");
        c.Check(Run("call me then insert my LinkedIn. Here we go") == $"Call me then {Url}. Here we go.", "a sentence end ends the command; Here after it is words");
        c.Check(Run("insert my LinkedIn here", []) is null, "no cues, no match");
        // Expansions are never matched again.
        c.Check(Run("insert my email here", [Cue("my email", "insert my LinkedIn here")]) == "insert my LinkedIn here", "no second pass");
        c.Done();
    }

    [Fact]
    public void Joining()
    {
        var c = new Checker();
        c.Check(Run("Insert my email here and call me") == $"{Email} and call me.", "starts with a command");
        c.Check(Run("Write to me at paste my email") == $"Write to me at {Email}", "ends with a command, no period added");
        c.Check(Run("Here is my page insert my LinkedIn here. Thanks for reading") == $"Here is my page {Url}. Thanks for reading.", "capital kept after a sentence end");
        c.Check(Run("Email me at insert my email here, thanks") == $"Email me at {Email}, thanks.", "no space before a comma");
        c.Check(Run("Is it insert my email here?") == $"Is it {Email}?", "no space before a question mark");
        c.Check(Run("insert my email here and insert my LinkedIn here and so on") == $"{Email} and {Url} and so on.", "middle segment is not capitalized and keeps no period");
        c.Check(Run("Here is my page. insert my LinkedIn here") == $"Here is my page. {Url}", "a period the speaker supplied stays");
        c.Check(Run("Please sign insert my signature here Next week") == "Please sign Thanks,\nSam\nNext week.", "no space after an expansion that ends a line; capital after a line break");
        c.Check(Run("insert my email here I will reply") == $"{Email} I will reply.", "I is never lowered");
        c.Check(Run("insert my email here, i will reply") == $"{Email}, I will reply.", "cleaned I stays a capital");
        c.Check(Run("insert my email here If you can") == $"{Email} If you can.", "a capital the speaker was heard to use stays");
        c.Check(Run("insert my email here akme said hi", keepsCase: w => w == "akme") == $"{Email} Akme said hi.", "a dictionary spelling keeps its capital");
        c.Check(Run("insert my email here akme said hi") == $"{Email} akme said hi.", "otherwise the first word keeps its spoken case");
        c.Check(Run("send insert my address here please") == $"Send {Address} please.", "multi-line expansion in a sentence");
        c.Check(Run("see  insert my email here  now") == $"See {Email} now.", "extra spaces collapse around the command");
        c.Done();
    }

    [Fact]
    public void Store()
    {
        var c = new Checker();
        var dir = TempDirs.New("cues");
        var cues = new VoiceCueStore(dir);
        var added = cues.Add("  my   LinkedIn ", Url);
        c.Check(added.Phrase == "my LinkedIn" && cues.Cues.Count == 1, "phrase whitespace collapses");
        cues.Add("my address", Address + "\n\nwith  odd   spacing ");
        void Rejects(string phrase, string text, string label) => c.Check(Throws(() => cues.Add(phrase, text)), label);
        Rejects("", "x", "empty phrase"); Rejects("1234", "x", "a phrase needs a letter"); Rejects(new string('a', 61), "x", "phrase over 60");
        Rejects("Insert", "x", "reserved insert"); Rejects(" paste ", "x", "reserved paste"); Rejects("Here!", "x", "reserved here");
        Rejects("MY  linkedin!", "x", "duplicate after normalization"); Rejects("my linked-in", "y", "hyphen does not make a new phrase");
        Rejects("ok phrase", "", "empty text"); Rejects("ok phrase", " \n ", "blank text"); Rejects("ok phrase", new string('a', 4001), "text over 4000");
        c.Check(!Throws(() => cues.Add(new string('a', 60), new string('b', 4000))), "limits are inclusive");
        c.Check(!Throws(() => cues.Add("insert something", "x")), "a phrase may contain a command word");

        if (!OperatingSystem.IsWindows())
            c.Check(File.GetUnixFileMode(dir) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
                    && File.GetUnixFileMode(Path.Combine(dir, VoiceCueStore.FileName)) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "owner-only directory and file");
        var again = new VoiceCueStore(dir);
        c.Check(again.Cues.SequenceEqual(cues.Cues) && !again.Unreadable, "round trip");
        c.Check(again.Cues[1].Text == Address + "\n\nwith  odd   spacing ", "text stored verbatim");

        again.Update(added.Id, "my LinkedIn page", "new");
        c.Check(again.Cues[0].Phrase == "my LinkedIn page" && again.Cues[0].CreatedAt == added.CreatedAt, "update keeps id and date");
        c.Check(Throws(() => again.Update(added.Id, "my address", "x")), "update cannot take another phrase");
        c.Check(Throws(() => again.Update(Guid.NewGuid(), "zzz", "x")), "update of a missing cue fails");
        again.Delete(added.Id);
        c.Check(new VoiceCueStore(dir).Cues.Count == again.Cues.Count && !again.Cues.Any(x => x.Id == added.Id), "delete persists");

        // A damaged file leaves the store empty and read-only, and is not overwritten.
        var file = Path.Combine(dir, VoiceCueStore.FileName);
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes("not json"));
        var damaged = new VoiceCueStore(dir);
        c.Check(damaged.Unreadable && damaged.Cues.Count == 0 && Throws(() => damaged.Add("x y", "z")), "damaged file: empty and read-only");
        c.Check(File.ReadAllBytes(file).SequenceEqual(Encoding.UTF8.GetBytes("not json")), "damaged file untouched");
        // After Delete All Data the directory is gone: an empty store.
        Directory.Delete(dir, true);
        c.Check(new VoiceCueStore(dir).Cues.Count == 0, "absent file is an empty store");
        c.Done();
    }

    static bool Throws(Action action)
    {
        try { action(); return false; } catch (StoreError) { return true; }
    }
}
