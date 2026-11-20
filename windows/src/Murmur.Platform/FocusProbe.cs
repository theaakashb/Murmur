using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Murmur.Platform;

/// <summary>Identity and security metadata of the focused UI Automation element. Value and Selection are null for password fields (never read) and when not readable.</summary>
public sealed record FocusInfo(int ProcessId, string ProcessName, nint WindowHandle, int[] RuntimeId, int ControlType, bool IsPassword, string? Value, string? Selection, bool IsMurmur)
{
    /// <summary>Same process and same automation element (runtime ID); the Windows counterpart of the Mac's CFEqual on the element.</summary>
    public bool SameElement(FocusInfo? other) => other is not null && ProcessId == other.ProcessId && RuntimeId.Length > 0 && RuntimeId.AsSpan().SequenceEqual(other.RuntimeId);
}

/// <summary>
/// Focused element through UI Automation (COM). Works across processes only from the logged-in desktop session.
/// UIA calls into the target app and can block while it hangs: call from a worker thread, not the hook or UI thread.
/// </summary>
public static partial class FocusProbe
{
    const int ProcessIdProp = 30002, ControlTypeProp = 30003, IsPasswordProp = 30019, NativeWindowHandleProp = 30020, ValueValueProp = 30045, TextPatternId = 10014;
    const int MaxText = 100_000;

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IUIAutomationElement a, IUIAutomationElement b, out int same);
        [PreserveSig] int CompareRuntimeIds([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] a, [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] b, out int same);
        [PreserveSig] int GetRootElement(out IUIAutomationElement root);
        [PreserveSig] int ElementFromHandle(nint hwnd, out IUIAutomationElement element);
        [PreserveSig] int ElementFromPoint(long point, out IUIAutomationElement element);
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement element);
        [PreserveSig] int GetRootElementBuildCache(nint cache, out IUIAutomationElement root);
        [PreserveSig] int ElementFromHandleBuildCache(nint hwnd, nint cache, out IUIAutomationElement element);
        [PreserveSig] int ElementFromPointBuildCache(long point, nint cache, out IUIAutomationElement element);
        [PreserveSig] int GetFocusedElementBuildCache(nint cache, out IUIAutomationElement element);
        [PreserveSig] int CreateTreeWalker(nint condition, out IUIAutomationTreeWalker walker);
        [PreserveSig] int get_ControlViewWalker(out IUIAutomationTreeWalker walker);
        [PreserveSig] int get_ContentViewWalker(out IUIAutomationTreeWalker walker);
        [PreserveSig] int get_RawViewWalker(out IUIAutomationTreeWalker walker);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] out int[] runtimeId);
        [PreserveSig] int FindFirst(int scope, nint condition, out IUIAutomationElement found);
        [PreserveSig] int FindAll(int scope, nint condition, out nint found);
        [PreserveSig] int FindFirstBuildCache(int scope, nint condition, nint cache, out IUIAutomationElement found);
        [PreserveSig] int FindAllBuildCache(int scope, nint condition, nint cache, out nint found);
        [PreserveSig] int BuildUpdatedCache(nint cache, out IUIAutomationElement updated);
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, int ignoreDefault, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetCachedPropertyValueEx(int propertyId, int ignoreDefault, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetCurrentPatternAs(int patternId, ref Guid riid, out nint pattern);
        [PreserveSig] int GetCachedPatternAs(int patternId, ref Guid riid, out nint pattern);
        [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object pattern);
    }

    [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationTextPattern
    {
        [PreserveSig] int RangeFromPoint(long point, out IUIAutomationTextRange range);
        [PreserveSig] int RangeFromChild(IUIAutomationElement child, out IUIAutomationTextRange range);
        [PreserveSig] int GetSelection(out IUIAutomationTextRangeArray ranges);
    }

    [ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationTextRangeArray
    {
        [PreserveSig] int get_Length(out int length);
        [PreserveSig] int GetElement(int index, out IUIAutomationTextRange range);
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationTextRange
    {
        [PreserveSig] int Clone(out IUIAutomationTextRange clone);
        [PreserveSig] int Compare(IUIAutomationTextRange range, out int same);
        [PreserveSig] int CompareEndpoints(int a, IUIAutomationTextRange range, int b, out int result);
        [PreserveSig] int ExpandToEnclosingUnit(int unit);
        [PreserveSig] int FindAttribute(int attribute, [MarshalAs(UnmanagedType.Struct)] object value, int backward, out IUIAutomationTextRange found);
        [PreserveSig] int FindText([MarshalAs(UnmanagedType.BStr)] string text, int backward, int ignoreCase, out IUIAutomationTextRange found);
        [PreserveSig] int GetAttributeValue(int attribute, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetBoundingRectangles(out nint rectangles);
        [PreserveSig] int GetEnclosingElement(out IUIAutomationElement element);
        [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string text);
    }

    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    static readonly Lazy<IUIAutomation?> Automation = new(() =>
    {
        try { return (IUIAutomation?)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))!); }
        catch (COMException) { return null; }
    });

    static T? Prop<T>(IUIAutomationElement e, int id) =>
        e.GetCurrentPropertyValue(id, out var v) == 0 && v is T t ? t : default;

    /// <summary>Null when nothing accessible has focus (no desktop session, secure desktop, or no UIA provider).
    /// <paramref name="readText"/> false skips the value and selection reads (identity and password flag only).</summary>
    public static FocusInfo? Capture(bool readText = true)
    {
        try
        {
            if (Automation.Value is not { } uia || uia.GetFocusedElement(out var element) != 0 || element is null) return null;
            int pid = Prop<int>(element, ProcessIdProp);
            if (pid == 0) return null;
            bool password = Prop<bool>(element, IsPasswordProp);
            int[] runtimeId = element.GetRuntimeId(out var ids) == 0 && ids is not null ? ids : [];
            nint hwnd = Prop<int>(element, NativeWindowHandleProp);
            if (hwnd == 0)
            {
                var fg = GetForegroundWindow();
                if (fg != 0 && GetWindowThreadProcessId(fg, out var fgPid) != 0 && fgPid == (uint)pid) hwnd = fg;
            }
            string? value = null, selection = null;
            if (readText && !password)
            {
                value = Prop<string>(element, ValueValueProp);
                selection = ReadSelection(element);
            }
            return new FocusInfo(pid, ProcessName(pid), hwnd, runtimeId, Prop<int>(element, ControlTypeProp), password, value, selection, pid == Environment.ProcessId);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException) { return null; }
    }

    static string? ReadSelection(IUIAutomationElement element)
    {
        if (element.GetCurrentPattern(TextPatternId, out var pattern) != 0 || pattern is not IUIAutomationTextPattern text) return null;
        if (text.GetSelection(out var ranges) != 0 || ranges.get_Length(out int n) != 0 || n < 1) return null;
        if (ranges.GetElement(0, out var range) != 0 || range.GetText(MaxText, out var s) != 0) return null;
        return s;
    }

    static string ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName.ToLowerInvariant(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return ""; }
    }
}
