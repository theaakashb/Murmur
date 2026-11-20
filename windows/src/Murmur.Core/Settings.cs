// Port of app/Settings.swift.
using System.Text.Json;

namespace Murmur.Core;

// Each persisted user setting's key and default, declared once. Keys and defaults come from windows/shared/settings.json, which
// windows/shared/export_settings.swift writes from Prefs (the Mac default, plus the Windows default where spec/settings.md says it differs).

/// <summary>A key and its default. A missing or wrongly typed stored value reads as the default.</summary>
public class Setting<T>(string key, T fallback)
{
    public string Key { get; } = key;
    public T Fallback { get; } = fallback;

    /// <summary>The value for a stored one, or the default when it is missing or has the wrong type.</summary>
    public virtual T Read(object? stored)
    {
        if (stored is T same) return same;
        if (typeof(T) == typeof(int) && stored is long l && l is >= int.MinValue and <= int.MaxValue) return (T)(object)(int)l;
        if (typeof(T) == typeof(long) && stored is int i) return (T)(object)(long)i;
        if (typeof(T) == typeof(double) && stored is long d) return (T)(object)(double)d;
        // An empty JSON array carries no element type, so it reads as an empty list of either kind.
        if (typeof(T) == typeof(IReadOnlyList<double>) && stored is IReadOnlyList<string> { Count: 0 }) return (T)(object)Array.Empty<double>();
        return Fallback;
    }

    /// <summary>The form handed to <see cref="ISettingsStore"/>: bool, string, long, double or a list.</summary>
    public virtual object? Write(T value) => value switch { int i => (long)i, _ => value };
}

/// <summary>Swift's <c>RawRepresentable</c> subscript: an unknown or missing raw string reads as the default.</summary>
public sealed class EnumSetting<T>(string key, T fallback, Func<T, string> raw) : Setting<T>(key, fallback) where T : struct, Enum
{
    public override T Read(object? stored)
    {
        if (stored is string s) foreach (var value in Enum.GetValues<T>()) if (raw(value) == s) return value;
        return Fallback;
    }
    public override object? Write(T value) => raw(value);
}

public static class SettingsStoreExtensions
{
    public static T Get<T>(this ISettingsStore store, Setting<T> setting) => setting.Read(store.Get(setting.Key));
    public static void Set<T>(this ISettingsStore store, Setting<T> setting, T value) => store.Set(setting.Key, setting.Write(value));
}

/// <summary>Where the dictation indicator and its cards sit. Windows has no notch: top or bottom of the screen (raw "top", "bottom").</summary>
public enum DictationIndicator { Top, Bottom }

/// <summary>Where the live meeting indicator sits once the meeting panel is minimized: near the top of the screen, or in the tray (raw "top", "tray").</summary>
public enum MeetingIndicator { Top, Tray }

public static class IndicatorExtensions
{
    public static string Raw(this DictationIndicator value) => value == DictationIndicator.Top ? "top" : "bottom";
    public static string Raw(this MeetingIndicator value) => value == MeetingIndicator.Top ? "top" : "tray";
}

public static class MeetingIndicators
{
    /// <summary>The tray item's text while it is the indicator.</summary>
    public static string TrayTitle(string elapsed) => " " + elapsed;
}

/// <summary>Every setting's key and default (Swift <c>Prefs</c>, with the controller's shortcut and model folder keys).</summary>
public static class Prefs
{
    sealed record Row(string Key, string Type, JsonElement Mac, JsonElement? Windows);

    static readonly Dictionary<string, Row> Table = Load();

    static Dictionary<string, Row> Load()
    {
        var rows = new Dictionary<string, Row>();
        foreach (var row in SharedData.Load<JsonElement>("settings").GetProperty("settings").EnumerateArray())
        {
            rows[row.GetProperty("key").GetString()!] = new Row(row.GetProperty("key").GetString()!, row.GetProperty("type").GetString()!,
                row.GetProperty("mac").Clone(), row.TryGetProperty("windows", out var w) ? w.Clone() : null);
        }
        return rows;
    }

    /// <summary>The Windows default where the table has one, else the Mac's.</summary>
    static JsonElement Default(string key, string type)
    {
        var row = Table[key];
        if (row.Type != type) throw new InvalidDataException($"Setting '{key}' is {row.Type}, not {type}.");
        return row.Windows ?? row.Mac;
    }

    static Setting<bool> Bool(string key) => new(key, Default(key, "bool").GetBoolean());
    static Setting<int> Int(string key) => new(key, Default(key, "int").GetInt32());
    static Setting<string> Text(string key) => new(key, Default(key, "string").GetString()!);
    static Setting<IReadOnlyList<string>> StringList(string key) => new(key, Default(key, "stringList").EnumerateArray().Select(e => e.GetString()!).ToArray());
    static Setting<IReadOnlyList<double>> Doubles(string key) => new(key, Default(key, "doubleList").EnumerateArray().Select(e => e.GetDouble()).ToArray());
    static EnumSetting<T> Raw<T>(string key, Func<T, string> raw) where T : struct, Enum
    {
        var text = Default(key, "string").GetString();
        return new(key, Enum.GetValues<T>().First(v => raw(v) == text), raw);
    }

    public static readonly Setting<bool> CleanupFillers = Bool("cleanupFillers");
    public static readonly Setting<bool> CleanupCorrections = Bool("cleanupCorrections");
    public static readonly Setting<bool> CleanupLists = Bool("cleanupLists");
    public static readonly Setting<bool> CleanupPunctuation = Bool("cleanupPunctuation");
    public static readonly Setting<bool> CleanupPolish = Bool("cleanupPolish");
    public static readonly Setting<bool> CleanupProfessional = Bool("cleanupProfessional");
    public static readonly Setting<bool> CleanupSpoken = Bool("cleanupSpokenCommands");
    /// <summary>"local" or "cloud" (Swift <c>CleanupRoute</c>, which the Processing port owns).</summary>
    public static readonly Setting<string> CleanupRoute = Text("cleanupRoute");
    public static readonly Setting<bool> LearningEnabled = Bool("learningEnabled");
    public static readonly Setting<bool> ReadableLearning = Bool("learningRequiresReadableField");
    /// <summary>Screen context for AI cleanup routes: opt-in, off by default. A writing mode can override it.</summary>
    public static readonly Setting<bool> ScreenContext = Bool("cleanupScreenContext");
    public static readonly EnumSetting<HistoryMode> HistoryMode = Raw<HistoryMode>("historyMode", HistoryModes.Raw);
    public static readonly Setting<bool> UpdateChecks = Bool("updateChecksEnabled");
    public static readonly EnumSetting<HistoryRetention> Retention = Raw<HistoryRetention>("retention", HistoryRetentions.Raw);
    public static readonly Setting<bool> ShowMenuBar = Bool("showMenuBar");
    public static readonly Setting<bool> ShowInDock = Bool("showInDock");
    /// <summary>A WASAPI endpoint ID; empty is the system default.</summary>
    public static readonly Setting<string> InputDeviceUID = Text("inputDeviceUID");
    public static readonly Setting<bool> IntelligentLeadingSpace = Bool("intelligentLeadingSpace");
    public static readonly Setting<bool> AutomaticInsertion = Bool("automaticInsertion");
    public static readonly Setting<bool> PressToToggle = Bool("pressToToggle");
    public static readonly Setting<bool> RecordingSounds = Bool("recordingSounds");
    public static readonly Setting<bool> KeepRecordings = Bool("keepRecordings");
    public static readonly Setting<bool> LiveWords = Bool("liveWords");
    public static readonly Setting<bool> LiveWordsOnCloud = Bool("liveWordsOnCloud");
    public static readonly Setting<bool> UseGpuWhenAvailable = Bool("useGpuWhenAvailable");
    public static readonly Setting<int> SettingsSection = Int("settingsSection");
    public static readonly Setting<bool> SidebarCollapsed = Bool("sidebarCollapsed");
    public static readonly Setting<bool> ShowDetailedSettingsInfo = Bool("showDetailedSettingsInfo");
    public static readonly Setting<bool> MeetingPanelCollapsed = Bool("meetingPanelCollapsed");
    public static readonly EnumSetting<DictationIndicator> DictationIndicator = Raw<DictationIndicator>("dictationIndicator", IndicatorExtensions.Raw);
    public static readonly EnumSetting<MeetingIndicator> MeetingIndicator = Raw<MeetingIndicator>("meetingIndicator", IndicatorExtensions.Raw);
    /// <summary>The live meeting panel's top-right corner in screen pixels, x then y.</summary>
    public static readonly Setting<IReadOnlyList<double>> MeetingPanelTopRight = Doubles("meetingPanelTopRight");
    public static readonly Setting<bool> OpenAtLoginDefaulted = Bool("openAtLoginDefaulted");
    /// <summary>Folded "heard" words of dismissed dictionary suggestions. Derived from dictations, so Delete All removes it.</summary>
    public static readonly Setting<IReadOnlyList<string>> DismissedSuggestions = StringList("dictionarySuggestionsDismissed");

    // Controller+Settings.swift.
    /// <summary>A Windows preset (default <c>right-ctrl</c>) or <c>custom:...</c> with virtual-key codes.</summary>
    public static readonly Setting<string> Shortcut = Text("shortcut");
    /// <summary><c>same</c> means the dictation key with a selection.</summary>
    public static readonly Setting<string> VoiceEditShortcut = Text("voiceEditShortcut");
    /// <summary>Empty until the app computes the folder under %LOCALAPPDATA%\Murmur\Models.</summary>
    public static readonly Setting<string> ModelDirectory = Text("modelDirectory");
    /// <summary>New on Windows: the Mac's paste-last chord is fixed.</summary>
    public static readonly Setting<string> PasteLastShortcut = Text("pasteLastShortcut");

    /// <summary>Every key in the table, for checks that every default loads and for the coordinator's route-key cross-check.</summary>
    public static IReadOnlyCollection<string> Keys => Table.Keys;
}
