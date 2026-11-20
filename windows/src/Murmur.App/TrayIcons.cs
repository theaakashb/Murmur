using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.UI.Dispatching;

namespace Murmur.App;

/// <summary>The tray mark (the Mac's five bars): a black icon for a light taskbar, a white one for a dark taskbar, at the size the system tray wants for the current scaling.</summary>
internal static class TrayIcons
{
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    const int SmCxSmIcon = 49;

    /// <summary>The taskbar's theme, not the apps' (SystemUsesLightTheme); light unless the user chose dark.</summary>
    public static bool TaskbarIsLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 1) is not 0;

    public static System.Drawing.Icon Load()
    {
        int size = GetSystemMetricsForDpi(SmCxSmIcon, GetDpiForSystem());
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", TaskbarIsLight() ? "tray-light.ico" : "tray-dark.ico");
        return new System.Drawing.Icon(path, new System.Drawing.Size(size, size));
    }

    /// <summary>Sets the icon now and again whenever the system colours change.</summary>
    public static void Keep(H.NotifyIcon.TaskbarIcon tray, DispatcherQueue ui, Windows.UI.ViewManagement.UISettings settings)
    {
        tray.Icon = Load();
        settings.ColorValuesChanged += (_, _) => ui.TryEnqueue(() => tray.Icon = Load());
    }
}
