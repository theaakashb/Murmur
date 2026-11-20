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
    readonly MenuFlyout menu = new() { MenuFlyoutPresenterStyle = MurmurStyle.Style("MurmurTrayMenu") };
    bool open, openedFromKeyboard;
    MenuFlyoutItem? meetingItem;
    Func<bool>? meetingLive;
    MenuFlyoutItem? updateItem;
    Func<string?>? updateVersion;

    public TrayMenu(Action settings, Action history, Action quit, Action? toggleMeeting = null, Func<bool>? meetingLive = null, Action? restartToUpdate = null, Func<string?>? updateVersion = null)
    {
        window.Content = anchor;
        window.AppWindow.SetPresenter(OverlappedPresenter.CreateForContextMenu());
        window.AppWindow.IsShownInSwitchers = false;
        hwnd = WindowNative.GetWindowHandle(window);
        var style = (long)Native.GetWindowLongPtr(hwnd, Native.GwlExStyle);
        Native.SetWindowLongPtr(hwnd, Native.GwlExStyle, (nint)(style | Native.WsExLayered));
        Native.SetLayeredWindowAttributes(hwnd, 0, 0, 2);
        var settingsItem = Item("Settings…", settings);
        menu.Items.Add(settingsItem);
        menu.Items.Add(Item("History…", history));
        if (toggleMeeting is not null) { meetingItem = Item("Start meeting", toggleMeeting); menu.Items.Add(meetingItem); this.meetingLive = meetingLive; }
        if (restartToUpdate is not null) { updateItem = Item("Restart to update", restartToUpdate); updateItem.Visibility = Visibility.Collapsed; menu.Items.Add(updateItem); this.updateVersion = updateVersion; }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Quit Murmur", quit));
        menu.Opened += (_, _) =>
        {
            if (!openedFromKeyboard) settingsItem.Focus(FocusState.Pointer);
        };
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
        openedFromKeyboard = fromKeyboard;
        if (updateItem is not null) updateItem.Visibility = updateVersion?.Invoke() is null ? Visibility.Collapsed : Visibility.Visible;
        if (meetingItem is not null) meetingItem.Text = meetingLive?.Invoke() == true ? "Stop meeting" : "Start meeting";
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

    MenuFlyoutItem Item(string text, Action action)
    {
        var item = MurmurStyle.Named(new MenuFlyoutItem { Text = text, FontSize = 13 }, text);
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
