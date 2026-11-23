using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Murmur.App;

/// <summary>Replaces the XAML-generated Main: Velopack must run first, because install, update and uninstall hooks start this exe and exit.</summary>
public static class Program
{
    /// <summary>The first launch after Setup.exe (Velopack passes it once): the app then shows its window, so the install visibly finished.</summary>
    internal static bool JustInstalled { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        // Uninstall removes the install folder but not our HKCU Run value; take it out so Windows doesn't keep starting a missing launcher.
        VelopackApp.Build().OnBeforeUninstallFastCallback(_ => LoginItem.Set(false)).OnFirstRun(_ => JustInstalled = true).Run();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
