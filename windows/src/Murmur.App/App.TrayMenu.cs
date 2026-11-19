using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;
using WinRT.Interop;

namespace Murmur.App;

internal sealed class TrayMenu
{
    readonly Window window = new();
    readonly nint hwnd;
    readonly Grid anchor = new();
    readonly MenuFlyout menu = new();
    bool open;

    public TrayMenu(Action settings, Action history, Action quit)
    {
        window.Content = anchor;
        window.AppWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());
        window.AppWindow.IsShownInSwitchers = false;
        hwnd = WindowNative.GetWindowHandle(window);
        var style = (long)Native.GetWindowLongPtr(hwnd, Native.GwlExStyle);
        Native.SetWindowLongPtr(hwnd, Native.GwlExStyle, (nint)(style | Native.WsExLayered));
        Native.SetLayeredWindowAttributes(hwnd, 0, 0, 2);
        menu.Items.Add(Item("Settings", Symbol.Setting, settings));
        menu.Items.Add(Item("History", Symbol.Clock, history));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Quit Murmur", Symbol.Cancel, quit));
        menu.Closed += (_, _) =>
        {
            open = false;
            window.AppWindow.Hide();
        };
        anchor.Loaded += (_, _) => { if (open) ShowFlyout(); };
    }

    public void Show(bool fromKeyboard = false)
    {
        if (open) return;
        open = true;
        GetCursorPos(out var cursor);
        var work = (fromKeyboard ? DisplayArea.Primary : DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Primary)).WorkArea;
        if (fromKeyboard) cursor = new PointInt32(work.X + work.Width - 8, work.Y + work.Height - 8);
        Native.SetWindowPos(hwnd, Native.HwndTopmost,
            Math.Clamp(cursor.X, work.X + 1, work.X + work.Width - 1),
            Math.Clamp(cursor.Y, work.Y + 1, work.Y + work.Height - 1),
            1, 1, Native.SwpNoActivate);
        window.Activate();
        if (anchor.IsLoaded) ShowFlyout();
    }

    void ShowFlyout()
    {
        if (menu.IsOpen) return;
        menu.ShowAt(anchor, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
            ShowMode = FlyoutShowMode.Standard
        });
    }

    MenuFlyoutItem Item(string text, Symbol icon, Action action)
    {
        var item = Fluent.Named(new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon) }, text);
        item.Click += (_, _) =>
        {
            menu.Hide();
            action();
        };
        return item;
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out PointInt32 point);
}
