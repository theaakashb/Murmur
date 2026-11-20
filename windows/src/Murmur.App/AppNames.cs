using System.Diagnostics;
using Microsoft.Win32;
using Murmur.Core;

namespace Murmur.App;

/// <summary>History stores a lowercased process name ("chrome"). The Mac shows the app's display name; here it is the call-app catalog name, else the executable's FileDescription
/// (found through App Paths, or a running process of that name), else the capitalised name. Cached for the run.</summary>
internal static class AppNames
{
    static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Display(string app)
    {
        if (Cache.TryGetValue(app, out var known)) return known;
        var process = CallApp.Normalize(app);
        var name = CallApp.Find(app)?.Name ?? Description(process) ?? Capitalised(process, app);
        return Cache[app] = name;
    }

    static string RemoveSuffix(this string text, string suffix) => text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? text[..^suffix.Length] : text;

    static string Capitalised(string name, string original) => name.Length == 0 ? original : char.ToUpper(name[0], System.Globalization.CultureInfo.CurrentCulture) + name[1..];

    static string? Description(string process)
    {
        try
        {
            string? path = null;
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{process}.exe");
                path = key?.GetValue(null) as string;
                if (path is not null) break;
            }
            if (path is null || !File.Exists(path = Environment.ExpandEnvironmentVariables(path.Trim('"'))))
            {
                path = null;
                foreach (var running in Process.GetProcessesByName(process))
                    using (running) { try { path ??= running.MainModule?.FileName; } catch (Exception) { } }
            }
            return path is not null && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } text ? text.Trim().RemoveSuffix(".exe") : null;
        }
        catch (Exception) { return null; }
    }
}
