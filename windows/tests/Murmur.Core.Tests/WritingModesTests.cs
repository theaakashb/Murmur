// Ports the WritingModes, DictationStyle and settings-persistence parts of app/check_modes.swift: website patterns, trigger words, priority and order,
// persistence and preset seeding, override inheritance, preset triggers. (Tone transforms, the cleanup prompt and the processing pipeline with mock providers
// belong to the CleanupTypes/CleanupPrompt/Processing ports; the Mac's migrations of older saved modes are not ported.)
using Murmur.Core;

namespace Murmur.Core.Tests;

public class WritingModesTests
{
    static Uri U(string s) => new(s);
    static bool Site(string pattern, string url) => WritingModes.WebsiteMatches(pattern, U(url));
    static string? Strip(string trigger, string text) => WritingModes.StripTrigger(trigger, text);

    [Fact]
    public void WebsitePatterns()
    {
        var c = new Checker();
        c.Check(Site("google.com", "https://mail.google.com/x"), "a subdomain matches its host");
        c.Check(Site("google.com", "https://google.com"), "the host itself matches");
        c.Check(!Site("google.com", "https://notgoogle.com"), "a host suffix without a dot does not match");
        c.Check(Site("*.slack.com", "https://acme.slack.com/client"), "*.host matches a subdomain");
        c.Check(Site("*.slack.com", "https://slack.com"), "*.host matches the bare host");
        c.Check(Site("github.com/org/*", "https://github.com/org/repo"), "host/path/* matches below the path");
        c.Check(Site("github.com/org", "https://github.com/org"), "host/path matches the path itself");
        c.Check(!Site("github.com/org/*", "https://github.com/organization"), "a path matches whole segments only");
        c.Check(!Site("github.com/org", "https://gist.github.com.evil.io/org"), "a host must end the URL's host");
        c.Check(Site("HTTPS://Docs.Google.com/Document/", "https://docs.google.com:443/document/d/1?x=1#y"), "scheme, case, port, query, fragment ignored");
        c.Check(Site("www.example.com", "https://example.com/a"), "www. in the pattern is ignored");
        c.Check(!Site("", "https://example.com"), "an empty pattern never matches");
        c.Check(Site("localhost:3000", "http://localhost:3000/app"), "a port in the pattern is ignored");
        c.Done();
    }

    [Fact]
    public void TriggerWords()
    {
        var c = new Checker();
        c.Check(Strip("slack mode", "Slack mode, fix the build.") == "Fix the build.", "leading trigger and its comma go, next word capitalized");
        c.Check(Strip("slack mode", "slack mode fix the build") == "fix the build", "lowercase transcript stays lowercase");
        c.Check(Strip("code", "Code: fix it") == "Fix it", "a colon after the trigger goes");
        c.Check(Strip("email mode", "Email mode. Fix it.") == "Fix it.", "a period after a leading trigger goes");
        c.Check(Strip("email mode", "E-mail mode, fix it.") == "Fix it.", "a hyphenated \"E-mail\" still matches the trigger");
        c.Check(Strip("slack mode", "Fix it, slack mode.") == "Fix it.", "trailing trigger: comma dropped, terminal mark kept");
        c.Check(Strip("slack mode", "Is it fixed slack mode?") == "Is it fixed?", "a trailing question mark is kept");
        c.Check(Strip("slack mode", "Fix it. Slack mode.") == "Fix it.", "a trailing trigger sentence leaves the earlier period");
        c.Check(Strip("code", "Codebase looks good") is null, "whole words only at the start");
        c.Check(Strip("code", "This is my barcode") is null, "whole words only at the end");
        c.Check(Strip("slack mode", "Tell them slack mode is great") is null, "a trigger mid-sentence does not match");
        c.Check(Strip("slack mode", "Slack mode.") is null, "a dictation that is only the trigger does not match");
        c.Check(Strip("slack mode", "Slack is down") is null, "part of a trigger does not match");
        c.Check(Strip("  ", "anything") is null, "a blank trigger never matches");
        c.Done();
    }

    [Fact]
    public void PriorityAndOrder()
    {
        var c = new Checker();
        var app = new WritingMode { Name = "App", Apps = ["slack.exe"], Tone = Tone.Casual };
        var site = new WritingMode { Name = "Site", Websites = ["mail.google.com"], Tone = Tone.Excited };
        var word = new WritingMode { Name = "Word", Triggers = ["formal mode"] };
        var off = new WritingMode { Name = "Off", Enabled = false, Apps = ["notepad.exe"], Websites = ["mail.google.com"], Triggers = ["formal mode"] };
        List<WritingMode> modes = [off, app, site, word];
        var gmail = U("https://mail.google.com/mail");
        c.Check(WritingModes.Match(modes, "notepad.exe", gmail)?.Name == "Site", "website beats app (and a disabled mode never matches)");
        c.Check(WritingModes.Match([app, new WritingMode { Name = "Site2", Websites = ["google.com"] }], "slack.exe", gmail)?.Name == "Site2", "website beats app across modes");
        c.Check(WritingModes.Match(modes, "slack.exe", null)?.Name == "App", "app matches without a URL");
        c.Check(WritingModes.Match(modes, "SLACK.EXE", null)?.Name == "App", "app identifiers compare without case");
        c.Check(WritingModes.Match(modes, "notepad.exe", null) is null, "no match: no mode");
        c.Check(WritingModes.Match(modes, null, null) is null && WritingModes.Match(modes, "", null) is null, "no identifier: no app match");
        var first = new WritingMode { Name = "First", Apps = ["x.app"] }; var second = new WritingMode { Name = "Second", Apps = ["x.app"] };
        c.Check(WritingModes.Match([first, second], "x.app", null)?.Name == "First", "within a tier the first mode wins");
        c.Check(!WritingModes.NeedsURL([app, off]) && WritingModes.NeedsURL([site]), "the URL is needed only when an enabled mode lists a website");
        c.Done();
    }

    [Fact]
    public void PersistenceAndPresets()
    {
        var c = new Checker();
        var store = new InMemorySettingsStore();
        var saved = new WritingMode { Name = "A", Apps = ["a"], Websites = ["b.com/c"], Triggers = ["d"], Options = CleanupOptions.Faithful, Tone = Tone.VeryCasual, ScreenContext = false, Instructions = "Short." };
        WritingModes.Save([saved], store);
        store.Set(WritingModes.PresetsSeededKey, false);
        c.Check(WritingModes.Load(store).SequenceEqual([saved, .. WritingMode.Presets]), "modes round-trip through the store, presets seeded after them");
        c.Check(WritingModes.Load(new InMemorySettingsStore()).SequenceEqual(WritingMode.Presets), "no saved modes loads as the presets");

        var ud = new InMemorySettingsStore();
        var custom = new WritingMode { Name = "Mine", Triggers = ["slack mode"] };
        WritingModes.Save([custom], ud);
        var seeded = WritingModes.Load(ud);
        c.Check(seeded.Select(m => m.Name).SequenceEqual(["Mine", "Prompt Mode", "Email", "Notes"]), "presets are added after existing modes");
        var edited = seeded.Where(m => m.Name != "Notes").ToList();
        WritingModes.Save(edited, ud);
        c.Check(WritingModes.Load(ud).Count == 3, "a deleted preset is not seeded again");
        WritingModes.Save([], ud);
        c.Check(WritingModes.Load(ud).Count == 0, "all presets can be deleted permanently");
        var cleared = WritingMode.Presets.Select(p => p with { Instructions = "" }).ToList();
        WritingModes.Save(cleared, ud);
        c.Check(WritingModes.Load(ud).SequenceEqual(cleared), "cleared instructions stay cleared on reload (no baseline migration)");

        const string damaged = "not json";
        ud.Set(WritingModes.DefaultsKey, damaged);
        c.Check(WritingModes.Load(ud).Count == 0 && ud.Get(WritingModes.UnreadableKey) is damaged, "an unreadable saved list is kept aside before a save can replace it");
        WritingModes.Save([custom], ud);
        c.Check(ud.Get(WritingModes.UnreadableKey) is damaged, "a later save leaves the kept-aside list alone");
        foreach (var preset in WritingMode.Presets) c.Check(preset.Instructions.Length > 0 && SwiftText.Count(preset.Instructions) <= WritingModeLimits.Instructions, "editable baseline fits the instruction field");

        // Through the settings file too: modes are stored as JSON text under one key.
        var dir = TempDirs.New("modes");
        var file = new JsonSettingsStore(Path.Combine(dir, "settings.json"));
        WritingModes.Save([saved], file);
        c.Check(new JsonSettingsStore(Path.Combine(dir, "settings.json")).Get(WritingModes.DefaultsKey) is string, "modes persist in settings.json");
        c.Done();
    }

    [Fact]
    public void OverrideInheritance()
    {
        var c = new Checker();
        var standard = CleanupOptions.Standard;
        bool Captures(WritingMode? mode, CleanupOptions options, bool global, params WritingMode[] modes) => DictationStyle.NeedsScreenContext(mode, modes, options, global);
        c.Check(!Captures(null, standard, true), "Strict does not read screen context with a stored AI provider");
        c.Check(!Captures(null, CleanupOptions.Faithful, true), "Faithful does not read screen context");
        c.Check(DictationStyle.NeedsScreenContext(null, [], standard, true, localModel: true), "Natural on the Local model reads opted-in context");
        c.Check(!DictationStyle.NeedsScreenContext(null, [], standard, false, localModel: true), "Natural on the Local model respects the context setting");
        c.Check(!DictationStyle.NeedsScreenContext(new WritingMode { Options = standard, Triggers = ["note"] }, [], standard, true, localModel: true), "a Natural writing mode runs on the AI engine, so the Local model reads nothing for it");
        var polish = new CleanupOptions(polish: true);
        c.Check(Captures(null, polish, true), "Polish reads opted-in context");
        c.Check(!Captures(new WritingMode { Options = standard }, polish, true), "Strict mode overrides a global rewrite");
        var trigger = new WritingMode { Transform = ModeTransform.AiPrompt, Triggers = ["prompt mode"] };
        c.Check(Captures(null, standard, true, trigger), "AI trigger can still need context under Strict");
        c.Check(!Captures(null, standard, true, trigger with { Enabled = false }), "Disabled triggers never read context");
        c.Check(!Captures(null, standard, true, trigger with { ScreenContext = false }), "Trigger opt-out never reads context");
        c.Check(Captures(null, standard, false, trigger with { ScreenContext = true }), "Trigger opt-in overrides the global switch");
        c.Check(!Captures(null, standard, false, trigger with { ScreenContext = true, Triggers = [] }), "Unmatched modes without triggers cannot read context");

        var options = standard with { Polish = true };
        var ctx = new ScreenContext { Selected = "Ayesha" };
        var inherit = DictationStyle.ForMode(new WritingMode { Name = "Inherit" }, options, true, ctx);
        c.Check(inherit.Options == options && inherit.Tone == Tone.Formal && inherit.Context == ctx && inherit.Instructions.Length == 0, "unset overrides inherit the settings");
        var over = DictationStyle.ForMode(new WritingMode { Options = CleanupOptions.Faithful, Tone = Tone.Casual, ScreenContext = false, Instructions = new string('x', 2_500) }, options, true, ctx);
        c.Check(over.Options == CleanupOptions.Faithful && over.Tone == Tone.Casual && over.Context is null, "set overrides replace the settings, and context off drops it");
        c.Check(SwiftText.Count(over.Instructions) == WritingModeLimits.Instructions, "instructions are capped at 2,000 characters");
        var trusting = options with { TrustModel = true };
        c.Check(DictationStyle.ForMode(new WritingMode { Options = CleanupOptions.Faithful }, trusting, false).Options.TrustModel, "trusting AI rewrites carries into a mode's options");
        c.Check(DictationStyle.ForMode(null, options, false, ctx).Context is null, "global context off and no mode: no context");
        c.Check(DictationStyle.ForMode(new WritingMode { ScreenContext = true, Options = options }, options, false, ctx).Context == ctx, "a mode turns context on over the global setting");
        c.Check(DictationStyle.ForMode(new WritingMode { ScreenContext = true }, CleanupOptions.Faithful, false, ctx).Context is null, "context never goes to a mode with every option off");
        c.Check(DictationStyle.ForMode(new WritingMode { ScreenContext = true }, standard, false, ctx).Context == ctx, "Natural keeps its context: the Local model cleans Natural with it");
        c.Check(DictationStyle.ForMode(WritingMode.Presets[0], standard, true, ctx) is { UsesEngine: true, UsesModel: true, Transform: ModeTransform.AiPrompt }, "a preset runs on the AI engine");
        c.Check(DictationStyle.ForMode(null, standard, true).Transform is null, "bypass style has no transform");
        c.Done();
    }

    [Fact]
    public void PresetTriggers()
    {
        var c = new Checker();
        var ps = WritingMode.Presets;
        var custom = new WritingMode { Name = "Mine", Triggers = ["slack mode"] };
        c.Check(WritingModes.Trigger(ps, "Prompt mode, fix the login bug")?.Text == "Fix the login bug", "preset trigger at the start");
        c.Check(WritingModes.Trigger(ps, "PROMPT MODE fix it")?.Mode.Transform == ModeTransform.AiPrompt, "preset trigger ignores case");
        c.Check(WritingModes.Trigger(ps, "Prompt engineer, fix the login bug")?.Text == "Fix the login bug", "legacy prompt engineer trigger still matches");
        c.Check(WritingModes.Trigger(ps, "fix the login bug, prompt mode") is null, "preset trigger at the end does not match");
        c.Check(WritingModes.Trigger(ps, "I said email mode is odd later") is null, "preset trigger in the middle does not match");
        c.Check(WritingModes.Trigger(ps, "notes mode") is null, "a phrase-only dictation does not match");
        c.Check(WritingModes.Trigger([custom], "Fix it, slack mode.")?.Text == "Fix it.", "custom modes still match at the end");
        var old = StoreJson.Decode<WritingMode>(StoreJson.Encode(custom));
        c.Check(old.Transform is null && old == custom, "a mode without a transform decodes as before");
        c.Done();
    }
}
