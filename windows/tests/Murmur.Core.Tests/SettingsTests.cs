// Tests for Settings.cs (port of app/Settings.swift) and JsonSettingsStore.cs.
using System.Reflection;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

public class SettingsTests
{
    static string SettingsPath() => Path.Combine(TempDirs.New("settings"), "settings.json");

    [Fact]
    public void EveryExportedKeyHasAPrefsEntryAndBack()
    {
        var keys = typeof(Prefs).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType.BaseType is { IsGenericType: true } || f.FieldType.IsGenericType)
            .Select(f => (string)f.FieldType.GetProperty("Key")!.GetValue(f.GetValue(null))!).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(Prefs.Keys.Order().ToArray(), keys.Order().ToArray());
        Assert.Contains("cleanupSpokenCommands", keys); // the Swift key for Prefs.cleanupSpoken
    }

    [Fact]
    public void DefaultsAreTheMacsExceptWhereTheSpecSaysWindowsDiffers()
    {
        var store = new InMemorySettingsStore();
        var c = new Checker();
        c.Check(store.Get(Prefs.CleanupFillers) && store.Get(Prefs.CleanupCorrections) && store.Get(Prefs.CleanupLists) && store.Get(Prefs.CleanupPunctuation) && store.Get(Prefs.CleanupSpoken), "cleanup comforts on");
        c.Check(!store.Get(Prefs.CleanupPolish) && !store.Get(Prefs.CleanupProfessional) && !store.Get(Prefs.ScreenContext), "rewrites and screen context off");
        c.Check(store.Get(Prefs.CleanupRoute) == "local", "local route");
        c.Check(store.Get(Prefs.LearningEnabled) && !store.Get(Prefs.ReadableLearning), "learning");
        c.Check(store.Get(Prefs.HistoryMode) == HistoryMode.On && store.Get(Prefs.Retention) == HistoryRetention.ThirtyDays, "history on, 30 days");
        c.Check(store.Get(Prefs.UpdateChecks) && store.Get(Prefs.AutomaticInsertion) && store.Get(Prefs.RecordingSounds), "on by default");
        c.Check(!store.Get(Prefs.PressToToggle) && !store.Get(Prefs.IntelligentLeadingSpace) && !store.Get(Prefs.LiveWords) && !store.Get(Prefs.LiveWordsOnCloud) && !store.Get(Prefs.KeepRecordings), "off by default");
        c.Check(store.Get(Prefs.InputDeviceUID) == "" && store.Get(Prefs.SettingsSection) == 0 && !store.Get(Prefs.SidebarCollapsed), "pane state");
        c.Check(store.Get(Prefs.MeetingPanelTopRight).Count == 0 && store.Get(Prefs.DismissedSuggestions).Count == 0, "empty lists");
        c.Check(store.Get(Prefs.DictationIndicator) == DictationIndicator.Bottom && store.Get(Prefs.MeetingIndicator) == MeetingIndicator.Tray, "Windows indicators (no notch)");
        c.Check(store.Get(Prefs.Shortcut) == "right-ctrl", "the dictation key is Right Ctrl on Windows");
        c.Check(store.Get(Prefs.PasteLastShortcut) == "ctrl-win-shift-v", "paste-last default");
        c.Check(store.Get(Prefs.VoiceEditShortcut) == "same" && store.Get(Prefs.ModelDirectory) == "", "voice edit follows the dictation key; the model folder is computed by the app");
        c.Done();
    }

    [Fact]
    public void ATypedValueReadsBackAndAWrongTypeReadsAsTheDefault()
    {
        var store = new InMemorySettingsStore();
        store.Set(Prefs.CleanupFillers, false);
        store.Set(Prefs.SettingsSection, 7);
        store.Set(Prefs.MeetingPanelTopRight, new double[] { 1280, 40.5 });
        store.Set(Prefs.DismissedSuggestions, new[] { "a", "b" });
        Assert.False(store.Get(Prefs.CleanupFillers));
        Assert.Equal(7, store.Get(Prefs.SettingsSection));
        Assert.Equal(new[] { 1280.0, 40.5 }, store.Get(Prefs.MeetingPanelTopRight));
        Assert.Equal(["a", "b"], store.Get(Prefs.DismissedSuggestions));

        var wrong = new InMemorySettingsStore(new Dictionary<string, object?>
        {
            ["cleanupFillers"] = "no", ["settingsSection"] = "3", ["inputDeviceUID"] = 5L, ["dictionarySuggestionsDismissed"] = true, ["retention"] = "next-week", ["historyMode"] = 1L,
            ["dictationIndicator"] = "notch", ["meetingPanelTopRight"] = new[] { "x" }, ["shortcut"] = false,
        });
        Assert.True(wrong.Get(Prefs.CleanupFillers));
        Assert.Equal(0, wrong.Get(Prefs.SettingsSection));
        Assert.Equal("", wrong.Get(Prefs.InputDeviceUID));
        Assert.Empty(wrong.Get(Prefs.DismissedSuggestions));
        Assert.Equal(HistoryRetention.ThirtyDays, wrong.Get(Prefs.Retention)); // an unknown retention reads as 30 days
        Assert.Equal(HistoryMode.On, wrong.Get(Prefs.HistoryMode));
        Assert.Equal(DictationIndicator.Bottom, wrong.Get(Prefs.DictationIndicator));
        Assert.Empty(wrong.Get(Prefs.MeetingPanelTopRight));
        Assert.Equal("right-ctrl", wrong.Get(Prefs.Shortcut));
        // An integer setting that was stored out of range reads as the default.
        Assert.Equal(0, new InMemorySettingsStore(new Dictionary<string, object?> { ["settingsSection"] = long.MaxValue }).Get(Prefs.SettingsSection));
    }

    [Fact]
    public void RawSettingsStoreTheRawString()
    {
        var store = new InMemorySettingsStore();
        store.Set(Prefs.Retention, HistoryRetention.SixMonths);
        store.Set(Prefs.HistoryMode, HistoryMode.Off);
        store.Set(Prefs.DictationIndicator, DictationIndicator.Top);
        store.Set(Prefs.MeetingIndicator, MeetingIndicator.Top);
        Assert.Equal("six-months", store.Get("retention"));
        Assert.Equal("off", store.Get("historyMode"));
        Assert.Equal("top", store.Get("dictationIndicator"));
        Assert.Equal("top", store.Get("meetingIndicator"));
        Assert.Equal(HistoryRetention.SixMonths, store.Get(Prefs.Retention));
        Assert.Equal(" 12:30", MeetingIndicators.TrayTitle("12:30"));
    }

    [Fact]
    public void ASetOnTheJsonStoreIsReadBackAfterReopening()
    {
        var path = SettingsPath();
        var store = new JsonSettingsStore(path);
        store.Set(Prefs.CleanupFillers, false);
        store.Set(Prefs.SettingsSection, 4);
        store.Set(Prefs.Retention, HistoryRetention.Forever);
        store.Set(Prefs.MeetingPanelTopRight, new double[] { 1280, 40.5 });
        store.Set(Prefs.DismissedSuggestions, new[] { "x", "y" });
        store.Set(Prefs.Shortcut, "custom:162:0:Ctrl");
        store.Set("tips.lifetimeSecondsSaved", 12.25);
        var again = new JsonSettingsStore(path);
        Assert.False(again.Get(Prefs.CleanupFillers));
        Assert.Equal(4, again.Get(Prefs.SettingsSection));
        Assert.Equal(HistoryRetention.Forever, again.Get(Prefs.Retention));
        Assert.Equal(new[] { 1280.0, 40.5 }, again.Get(Prefs.MeetingPanelTopRight));
        Assert.Equal(["x", "y"], again.Get(Prefs.DismissedSuggestions));
        Assert.Equal("custom:162:0:Ctrl", again.Get(Prefs.Shortcut));
        Assert.Equal(12.25, again.Get("tips.lifetimeSecondsSaved"));
        Assert.True(again.Get(Prefs.LearningEnabled), "a key never written reads as its default");
        Assert.Equal(store.Snapshot().Keys.Order().ToArray(), again.Snapshot().Keys.Order().ToArray());
    }

    [Fact]
    public void TheFileIsSortedPrivateAndLeavesNoTemporaryFiles()
    {
        var path = SettingsPath();
        var store = new JsonSettingsStore(path);
        store.Set("zeta", true); store.Set("alpha", "x"); store.Set("mid", 3L);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(["alpha", "mid", "zeta"], document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(p => Path.GetFileName(p)!).ToArray());
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void AMissingFileIsAnEmptyStoreAndNothingIsWrittenUntilASet()
    {
        var path = SettingsPath();
        var store = new JsonSettingsStore(path);
        Assert.Empty(store.Snapshot());
        Assert.Null(store.MovedAsideTo);
        Assert.False(File.Exists(path));
        store.Remove("nothing"); // removing what is not there writes nothing
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"text\"")]
    [InlineData("")]
    public void ACorruptFileIsMovedAsideNeverDiscarded(string content)
    {
        var path = SettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        var store = new JsonSettingsStore(path);
        Assert.Empty(store.Snapshot());
        Assert.NotNull(store.MovedAsideTo);
        Assert.Equal(content, File.ReadAllText(store.MovedAsideTo!));
        Assert.False(File.Exists(path), "the corrupt file is no longer where the next write goes");
        store.Set("a", true);
        Assert.Equal(content, File.ReadAllText(store.MovedAsideTo!));
        Assert.True(new JsonSettingsStore(path).Get("a") is true);
    }

    [Fact]
    public void ValuesTheStoreDoesNotModelAreKeptWhenTheFileIsRewritten()
    {
        var path = SettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"tips.dismissals":{"a":2,"b":1},"mixed":[1,"x"],"tips.done":["one"],"tips.rewrites":[1.5,2],"nothing":null,"n":3,"d":2.5}""");
        var store = new JsonSettingsStore(path);
        Assert.Equal(3L, store.Get("n"));
        Assert.Equal(2.5, store.Get("d"));
        Assert.Equal(new[] { "one" }, (string[])store.Get("tips.done")!);
        Assert.Equal(new[] { 1.5, 2.0 }, (double[])store.Get("tips.rewrites")!);
        Assert.Null(store.Get("nothing"));
        store.Set("added", "y");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(2, document.RootElement.GetProperty("tips.dismissals").GetProperty("a").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("mixed").GetArrayLength());
    }

    [Fact]
    public void ASetOfAnUnstorableValueThrowsAndChangesNothing()
    {
        var path = SettingsPath();
        var store = new JsonSettingsStore(path);
        store.Set("a", 1);
        Assert.Throws<ArgumentException>(() => store.Set("b", new object()));
        Assert.Throws<ArgumentException>(() => store.Set("c", double.NaN));
        Assert.Equal(["a"], store.Snapshot().Keys.ToArray());
        Assert.Equal(["a"], new JsonSettingsStore(path).Snapshot().Keys.ToArray());
        store.Set("a", null); // null removes
        Assert.Empty(new JsonSettingsStore(path).Snapshot());
    }

    [Fact]
    public void ASettingsFolderThatIsAFileIsRefused()
    {
        var folder = TempDirs.New("settings");
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        File.WriteAllText(folder, "x");
        Assert.Throws<StoreError>(() => new JsonSettingsStore(Path.Combine(folder, "settings.json")));
    }
}

public class JsonSettingsStoreBomTests
{
    [Fact]
    public void ReadsAFileWithAByteOrderMark()
    {
        var dir = Directory.CreateTempSubdirectory("murmur-bom-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("{\"transcriptionRoute\":\"cloud\"}")]);
            var store = new Murmur.Core.JsonSettingsStore(path);
            Assert.Null(store.MovedAsideTo);
            Assert.Equal("cloud", store.Get("transcriptionRoute"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
