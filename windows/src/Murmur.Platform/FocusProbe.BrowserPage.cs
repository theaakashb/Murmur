using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Murmur.Platform;

public static partial class FocusProbe
{
    const int AutomationIdProp = 30011, ClassNameProp = 30012, NameProp = 30005,
        HasKeyboardFocusProp = 30008, IsOffscreenProp = 30022, EditControl = 50004,
        DocumentControl = 50030, ValuePatternId = 10002;
    const int MaxAncestors = 40, MaxChromeNodes = 400;
    static readonly ConcurrentDictionary<int, DateTime> PrimedBrowsers = new();

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationTreeWalker
    {
        [PreserveSig] int GetParentElement(IUIAutomationElement element, out IUIAutomationElement? parent);
        [PreserveSig] int GetFirstChildElement(IUIAutomationElement element, out IUIAutomationElement? first);
        [PreserveSig] int GetLastChildElement(IUIAutomationElement element, out IUIAutomationElement? last);
        [PreserveSig] int GetNextSiblingElement(IUIAutomationElement element, out IUIAutomationElement? next);
    }

    [ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationValuePattern
    {
        [PreserveSig] int SetValue([MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_CurrentValue([MarshalAs(UnmanagedType.BStr)] out string value);
    }

    /// <summary>Reads a transient web address for an unchanged, non-password recording target. Call off the UI thread.</summary>
    public static Uri? ReadBrowserPage(FocusInfo? target, CancellationToken ct = default) =>
        ReadBrowserPage(target, out _, ct);

    internal static Uri? ReadBrowserPage(FocusInfo? target, out bool usedAddressBar, CancellationToken ct = default)
    {
        usedAddressBar = false;
        if (target is not { IsPassword: false, RuntimeId.Length: > 0 } ||
            BrowserPage.Supported(target.ProcessName) is not { } family) return null;

        var start = Stopwatch.GetTimestamp();
        bool InBudget() => !ct.IsCancellationRequested && Stopwatch.GetElapsedTime(start).TotalMilliseconds < 320;
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out uint pid) == 0 ||
                (pid != target.ProcessId && ProcessName((int)pid) != target.ProcessName) ||
                Automation.Value is not { } uia || uia.ElementFromHandle(hwnd, out var window) != 0 || window is null ||
                uia.get_RawViewWalker(out var walker) != 0 || walker is null) return null;

            if (family == BrowserPage.Family.Chromium && InBudget()) PrimeBrowser((int)pid, walker, window);
            if (!InBudget() || uia.GetFocusedElement(out var focused) != 0 || focused is null ||
                focused.GetRuntimeId(out var id) != 0 || id is null || !target.RuntimeId.AsSpan().SequenceEqual(id)) return null;

            IUIAutomationElement? document = null;
            IUIAutomationElement? node = focused;
            bool insideWindow = false, addressFocused = false;
            for (int depth = 0; depth < MaxAncestors && node is not null && InBudget(); depth++)
            {
                if (uia.CompareElements(node, window, out int same) != 0) return null;
                if (same != 0) { insideWindow = true; break; }
                // Keep the outermost document so a focused iframe cannot select its own site.
                int type = Prop<int>(node, ControlTypeProp);
                if (type == DocumentControl) document = node;
                if (type == EditControl && IsAddressBar(node, family)) addressFocused = true;
                if (walker.GetParentElement(node, out node) != 0) break;
            }

            if (!BrowserPage.CanRead(insideWindow && document is not null, addressFocused, Prop<bool>(focused, IsPasswordProp))) return null;

            bool StillFocused() => InBudget() && GetForegroundWindow() == hwnd &&
                uia.GetFocusedElement(out var current) == 0 && current is not null &&
                uia.CompareElements(focused, current, out int same) == 0 && same != 0;
            if (!StillFocused()) return null;

            if (family == BrowserPage.Family.Chromium &&
                BrowserPage.Parse(Prop<string>(document!, ValueValueProp), schemeOptional: false) is { } documentPage)
                return StillFocused() ? documentPage : null;

            var bar = FindAddressBar(walker, window, family, InBudget);
            if (bar is null || !StillFocused() || Prop<bool>(bar, HasKeyboardFocusProp) ||
                bar.GetCurrentPattern(ValuePatternId, out var pattern) != 0 || pattern is not IUIAutomationValuePattern value ||
                !StillFocused() || value.get_CurrentValue(out var address) != 0 || !StillFocused()) return null;

            usedAddressBar = true;
            return BrowserPage.Parse(address, schemeOptional: true);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    static void PrimeBrowser(int pid, IUIAutomationTreeWalker walker, IUIAutomationElement window)
    {
        DateTime started;
        try { using var process = Process.GetProcessById(pid); started = process.StartTime; }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or ArgumentException) { return; }
        if (PrimedBrowsers.TryGetValue(pid, out var previous) ? previous == started || !PrimedBrowsers.TryUpdate(pid, started, previous)
            : !PrimedBrowsers.TryAdd(pid, started)) return;
        // An explicit tree query brings up Chromium's accessibility provider. Never alter browser settings or focus.
        walker.GetFirstChildElement(window, out _);
    }

    static bool IsAddressBar(IUIAutomationElement element, BrowserPage.Family family)
    {
        var id = Prop<string>(element, AutomationIdProp);
        if (family == BrowserPage.Family.Firefox) return id == "urlbar-input";
        return id == "view_1012" || Prop<string>(element, ClassNameProp) == "OmniboxViewViews" ||
            Prop<string>(element, NameProp) == "Address and search bar";
    }

    static IUIAutomationElement? FindAddressBar(IUIAutomationTreeWalker walker, IUIAutomationElement window,
        BrowserPage.Family family, Func<bool> inBudget)
    {
        var queue = new Queue<IUIAutomationElement>();
        queue.Enqueue(window);
        for (int seen = 0; queue.TryDequeue(out var node) && seen < MaxChromeNodes && inBudget(); seen++)
        {
            int type = Prop<int>(node, ControlTypeProp);
            // Search browser chrome only. A webpage can name an edit control "Address and search bar".
            if (type == DocumentControl) continue;
            if (type == EditControl && IsAddressBar(node, family) && !Prop<bool>(node, IsOffscreenProp)) return node;
            walker.GetFirstChildElement(node, out var child);
            while (child is not null && queue.Count < MaxChromeNodes && inBudget())
            {
                queue.Enqueue(child);
                if (walker.GetNextSiblingElement(child, out child) != 0) break;
            }
        }
        return null;
    }
}
