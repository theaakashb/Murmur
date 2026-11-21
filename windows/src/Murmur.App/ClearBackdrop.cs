using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Murmur.App;

/// <summary>A window backdrop that paints nothing, so a window shows only what XAML draws on it (the pill's dots), with per-pixel alpha and smooth edges.</summary>
internal sealed class ClearBackdrop : SystemBackdrop
{
    // The backdrop target takes a Windows.UI.Composition brush, whose Compositor needs a Windows.System dispatcher queue on this thread (the XAML one is a different type).
    [StructLayout(LayoutKind.Sequential)] struct QueueOptions { public int Size; public int Type; public int Apartment; }
    [DllImport("CoreMessaging.dll")] static extern int CreateDispatcherQueueController(QueueOptions options, out nint controller);
    static nint queueController;

    static void EnsureSystemQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null || queueController != 0) return;
        _ = CreateDispatcherQueueController(new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), Type = 2, Apartment = 0 }, out queueController);
    }

    /// <summary>Dev diagnostics: whether the clear brush was connected, and the exception if not.</summary>
    internal static string Status = "not connected";

    Windows.UI.Composition.Compositor? compositor;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        try
        {
            EnsureSystemQueue();
            compositor ??= new Windows.UI.Composition.Compositor();
            connectedTarget.SystemBackdrop = compositor.CreateColorBrush(Colors.Transparent);
            Status = "connected";
        }
        catch (Exception e) { Status = "failed: " + e.GetType().Name + " " + e.Message; CrashLog.Write(e); } // the pill then keeps the default window fill rather than failing to start
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
    }
}
