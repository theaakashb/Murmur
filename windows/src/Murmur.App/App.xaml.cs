using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace Murmur.App;

/// <summary>Starts in the tray with no main window. One instance per data folder.</summary>
public partial class App : Application
{
    internal static TitleBarTheme CaptionTheme { get; private set; } = TitleBarTheme.UseDefaultAppMode;
    static Mutex? single;
    AppServices? services;
    Controller? controller;
    Bubble? bubble;
    TaskbarIcon? tray;
    SettingsWindow? settings;
    HistoryWindow? history;

    public App()
    {
        // The visual test override is available only with a throwaway data root.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MURMUR_DATA_DIR")))
        {
            switch (Environment.GetEnvironmentVariable("MURMUR_THEME"))
            {
                case "light": RequestedTheme = ApplicationTheme.Light; CaptionTheme = TitleBarTheme.Light; break;
                case "dark": RequestedTheme = ApplicationTheme.Dark; CaptionTheme = TitleBarTheme.Dark; break;
            }
        }
        InitializeComponent();
        UnhandledException += (_, e) => { e.Handled = true; CrashLog.Write(e.Exception); TestEvents.Emit("error", ("stage", "app"), ("reason", "unhandled")); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { Launch(); }
        catch (UnauthorizedAccessException e)
        {
            // Mac parity: transcripts are never stored in a folder Murmur can't make private (PrivateFiles). Say so instead of crashing.
            CrashLog.Write(e);
            Native.MessageBoxW(IntPtr.Zero, "Murmur can't make its data folder private, so it won't store dictations there. Make sure your Windows account owns the folder:\n\n" + (Environment.GetEnvironmentVariable("MURMUR_DATA_DIR") is { Length: > 0 } d ? d : Murmur.Core.MurmurDataLocations.Root), "Murmur", 0x10);
            Exit();
        }
        catch (Exception e) { CrashLog.Write(e); throw; }
    }

    void Launch()
    {
        var dataDir = Environment.GetEnvironmentVariable("MURMUR_DATA_DIR");
        var name = string.IsNullOrWhiteSpace(dataDir) ? @"Local\Murmur.App" : @"Local\Murmur.App.Test." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir))))[..16];
        single = new Mutex(true, name, out var first);
        if (!first) Environment.Exit(0);

        var queue = DispatcherQueue.GetForCurrentThread();
        services = new AppServices(queue);
        bubble = new Bubble(queue);
        controller = new Controller(services, bubble);

        var menu = new TrayMenu(OpenSettings, OpenHistory, Quit);
        var showMenu = new XamlUICommand();
        showMenu.ExecuteRequested += (_, _) => menu.Show();
        tray = new TaskbarIcon
        {
            ToolTipText = "Murmur",
            LeftClickCommand = showMenu,
            RightClickCommand = showMenu,
            IconSource = new GeneratedIconSource { Text = "M", Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.White), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 77, 111, 240)), FontSize = 36 },
        };
        tray.TrayIcon.MessageWindow.KeyboardEventReceived += (_, e) =>
        {
            if (e.KeyboardEvent is KeyboardEvent.ContextMenu or KeyboardEvent.KeySelect)
                queue.TryEnqueue(() => menu.Show(fromKeyboard: true));
        };
        tray.ForceCreate();
        TestEvents.Emit("ready");
        if (services.DataProblem is { } problem) bubble.ShowError(problem, 8);
        // Development aid: open a window at launch (the tray menu is the normal way).
        var launchArgs = Environment.GetCommandLineArgs();
        if (launchArgs.Contains("--settings")) OpenSettings();
        if (launchArgs.Contains("--history")) OpenHistory();
    }

    void OpenSettings()
    {
        if (settings is null) { settings = new SettingsWindow(services!, controller!); settings.Closed += (_, _) => settings = null; }
        settings.Activate();
    }

    void OpenHistory()
    {
        if (history is null) { history = new HistoryWindow(services!); history.Closed += (_, _) => history = null; }
        history.Reload();
        history.Activate();
    }

    void Quit()
    {
        controller?.Dispose();
        services?.Paste.FlushRestore();
        tray?.Dispose();
        Environment.Exit(0);
    }
}
