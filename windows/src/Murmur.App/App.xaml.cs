using Murmur.Core;
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
    /// <summary>Null until launch. Settings/tray bind to <c>Updater.UpdateReady</c>, <c>ReadyVersion</c>, <c>RestartToUpdate()</c> and <c>LoginItem</c>.</summary>
    internal static Updater? Updater { get; private set; }
    AppServices? services;
    Controller? controller;
    LiveMeetingPanel? liveMeetingPanel; // held for its refresh timer
    Bubble? bubble;
    TaskbarIcon? tray;
    readonly Windows.UI.ViewManagement.UISettings trayTheme = new(); // held so its change event keeps firing
    MainWindow? main;
    WelcomeWindow? welcome;
    ModelToast? toast;

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
        // A background thread's exception still ends the process; this leaves its type and stack behind first (content-free).
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception x) CrashLog.Write(x); TestEvents.Emit("error", ("stage", "thread"), ("reason", "unhandled")); };
        TaskScheduler.UnobservedTaskException += (_, e) => { CrashLog.Write(e.Exception); e.SetObserved(); };
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
        // Development aid: --bubble-demo <state> <seconds> [light|dark] shows the real pill window for a while and exits. Its own mutex, no services, no data folder, no clipboard.
        var args0 = Environment.GetCommandLineArgs();
        var demoAt = Array.IndexOf(args0, "--bubble-demo");
        if (demoAt >= 0)
        {
            single = new Mutex(true, @"Local\Murmur.BubbleDemo", out var demoFirst);
            if (!demoFirst) Environment.Exit(0);
            string demoState = demoAt + 1 < args0.Length ? args0[demoAt + 1] : "all";
            double demoSeconds = demoAt + 2 < args0.Length && double.TryParse(args0[demoAt + 2], System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 5;
            bool? dark = demoAt + 3 < args0.Length ? args0[demoAt + 3] switch { "dark" => true, "light" => false, _ => null } : null;
            _ = DemoThenExit(new Bubble(DispatcherQueue.GetForCurrentThread()) { Demo = true, ForceDark = dark }, demoState, demoSeconds);
            return;
        }
        var dataDir = Environment.GetEnvironmentVariable("MURMUR_DATA_DIR");
        var name =string.IsNullOrWhiteSpace(dataDir) ? @"Local\Murmur.App" : @"Local\Murmur.App.Test." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir))))[..16];
        single = new Mutex(true, name, out var first);
        if (!first) Environment.Exit(0);

        var queue = DispatcherQueue.GetForCurrentThread();
        services = new AppServices(queue);
        bubble = new Bubble(queue) { OpenHistory = OpenHistory };
        controller = new Controller(services, bubble);
        _ = new CallOfferPanel(services, controller, queue); // held by the controller's CallOffered event
        _ = new TipPanel(services, controller.TipCards, (pane, tab) => Main().ShowSettingsPane(pane, tab), () => Main().ShowHome()); // held by the presenter's events
        liveMeetingPanel = new LiveMeetingPanel(services, controller, queue);
        // Registered only here, after the single-instance check: a second process started by a notification click has already exited above, so it never takes over the clicks.
        toast = new ModelToast(queue, () => this.welcome is not null, OpenFromToast);
        var startedByToast = toast.Start();
        controller.ModelDownloadFinished += (kind, model, error) => toast.Finished(kind, model, error, ShortcutSpec.Label(services.Settings.Get(Prefs.Shortcut)));

        // Mac parity (main.swift:28): start at login is on after the first launch, then the user's choice.
        if (!services.Settings.Get(Prefs.OpenAtLoginDefaulted)) { if (LoginItem.Set(true)) services.Settings.Set(Prefs.OpenAtLoginDefaulted, true); }
        Updater = new Updater(Updater.ProductionSource(), () => services.Settings.Get(Prefs.UpdateChecks), () => controller.IsBusy);
        Updater.UpdateReady += version => TestEvents.Emit("update-ready", ("version", version));
        Updater.Start();

        var menu = new TrayMenu(OpenSettings, OpenHistory, Quit, () => { if (controller!.MeetingLive) controller.StopMeeting(); else controller.StartMeeting(); }, () => controller!.MeetingLive,
            () => { if (Updater?.RestartToUpdate() is { } message) bubble.ShowNotice(message, 5); }, () => Updater?.ReadyVersion);
        var showMenu = new XamlUICommand();
        showMenu.ExecuteRequested += (_, _) => menu.Show();
        tray = new TaskbarIcon
        {
            ToolTipText = "Murmur",
            LeftClickCommand = showMenu,
            RightClickCommand = showMenu,
        };
        TrayIcons.Keep(tray, queue, trayTheme);
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
        if (launchArgs.Contains("--home")) Main().ShowHome();
        if (launchArgs.Contains("--insights")) Main().ShowInsights();
        if (launchArgs.Contains("--meetings")) Main().ShowMeetings();
        // First launch, as on the Mac. A throwaway data folder (tests, the harness) never opens it unprompted: it would take the focus.
        var forced = launchArgs.Contains("--welcome");
        var welcome = forced || (string.IsNullOrWhiteSpace(dataDir) && new Murmur.Core.WelcomeGuide(services.Settings).OpensAtLaunch(services.Local.ModelFilesPresent));
        if (welcome) OpenWelcome();
        // Windows only: after Setup.exe a tray-only start looks like a failed install, so the first run opens Home (unless Welcome already shows).
        else if ((Program.JustInstalled && string.IsNullOrWhiteSpace(dataDir)) || startedByToast) Main().ShowHome();
        if (launchArgs.Contains("--bubble")) bubble.ShowListening();
        if (launchArgs.Contains("--start-meeting")) controller.StartMeeting();
    }

    static async Task DemoThenExit(Bubble pill, string state, double seconds)
    {
        try { await pill.DemoAsync(state, seconds); }
        catch (Exception e) { CrashLog.Write(e); }
        Environment.Exit(0);
    }

    // One shared window: Settings and History are pages inside it.
    MainWindow Main()
    {
        if (main is null) { main = new MainWindow(services!, controller!); main.Closed += (_, _) => main = null; }
        return main;
    }

    public void ShowWelcome() => OpenWelcome();

    void OpenWelcome()
    {
        if (welcome is null) { welcome = new WelcomeWindow(services!, controller!); welcome.Closed += (_, _) => welcome = null; }
        welcome.Activate();
    }

    /// <summary>A click on the "Murmur is ready" notification: the Welcome window if it is open, else Home; either one is restored if minimized.</summary>
    void OpenFromToast(ModelNoticeTarget target)
    {
        // A notification click hands this process the right to take the foreground; Activate() alone can leave the window behind the app in front.
        if (target == ModelNoticeTarget.Welcome && welcome is not null) { BringForward(welcome); return; }
        var window = Main();
        window.ShowHome();
        BringForward(window);
    }

    static void BringForward(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        Native.ShowWindow(hwnd, 9); // SW_RESTORE
        window.Activate();
        Native.SetForegroundWindow(hwnd);
    }

    void OpenSettings() => Main().ShowSettings();

    void OpenHistory() => Main().ShowHistory();

    void Quit()
    {
        // A meeting being recorded, or one that could not be saved, is at risk: ask first (MeetingRecorder.quitWarning on the Mac).
        if (controller?.Meetings.QuitWarning() is { } warning &&
            Native.MessageBoxW(IntPtr.Zero, warning.Detail + $"\n\nChoose Yes to {warning.Confirm.ToLowerInvariant()}, or No to go back.", warning.Title, 0x34 | 0x40000) != 6) return;
        controller?.Dispose();
        Updater?.ApplyOnExit();
        services?.Paste.FlushRestore();
        toast?.Stop();
        tray?.Dispose();
        Environment.Exit(0);
    }
}
