using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.UI.Dispatching;
using Murmur.Core;

namespace Murmur.App;

/// <summary>The "Murmur is ready" Windows notification after a model download (Windows only; wording and decisions are Murmur.Core's OnboardingText).
/// The app is unpackaged: Register() gives it a notification identity (registry entries under HKCU) and a COM activator, so a click while Murmur runs arrives as
/// NotificationInvoked. A click after Murmur quit starts a new process; the single-instance mutex in App.Launch ends any second process before it registers.</summary>
internal sealed class ModelToast
{
    readonly DispatcherQueue queue;
    readonly Action<ModelNoticeTarget> open;
    readonly Func<bool> welcomeOpen;
    bool registered;

    internal ModelToast(DispatcherQueue queue, Func<bool> welcomeOpen, Action<ModelNoticeTarget> open)
    {
        this.queue = queue; this.welcomeOpen = welcomeOpen; this.open = open;
    }

    /// <summary>Registers for notifications. True when this process was itself started by a notification click (Murmur was not running).</summary>
    internal bool Start()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, _) => queue.TryEnqueue(() => open(OnboardingText.NoticeTarget(welcomeOpen())));
            AppNotificationManager.Default.Register();
            registered = true;
            return Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification;
        }
        catch (Exception e) { CrashLog.Write(e); return false; }
    }

    /// <summary>Called for every finished download; shows nothing unless OnboardingText says so.</summary>
    internal void Finished(ModelNoticeKind kind, string modelName, ModelDownloadError? error, string shortcut)
    {
        if (!registered || !OnboardingText.ShouldNotify(error, Native.ForegroundIsThisProcess())) return;
        try
        {
            var logo = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "murmur-logo.png"));
            var notification = new AppNotificationBuilder()
                .AddArgument("action", "open")
                .AddText(OnboardingText.NoticeTitle)
                .AddText(OnboardingText.NoticeBody(kind, modelName, shortcut))
                .SetAppLogoOverride(logo, AppNotificationImageCrop.Default)
                .AddButton(new AppNotificationButton(OnboardingText.NoticeButton).AddArgument("action", "open"))
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception e) { CrashLog.Write(e); }
    }

    /// <summary>On quit: stops this process receiving clicks. The registry entries stay for the next start; the uninstall hook removes them.</summary>
    internal void Stop()
    {
        if (!registered) return;
        try { AppNotificationManager.Default.Unregister(); } catch (Exception) { }
        registered = false;
    }

    /// <summary>The Velopack uninstall hook: removes the registration (registry keys and activator) and any notification left in the Action Center.</summary>
    internal static void RemoveAll()
    {
        try { AppNotificationManager.Default.RemoveAllAsync().AsTask().Wait(5000); } catch (Exception) { }
        try { AppNotificationManager.Default.UnregisterAll(); } catch (Exception) { }
    }
}
