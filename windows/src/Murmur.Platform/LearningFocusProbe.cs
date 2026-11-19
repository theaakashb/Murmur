using System.Runtime.InteropServices;
using Murmur.Core;

namespace Murmur.Platform;

public static partial class FocusProbe
{
    // The learning reader needs the later DocumentRange and MoveEndpointByRange slots.
    [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ILearningTextPattern
    {
        [PreserveSig] int RangeFromPoint(long point, out IUIAutomationTextRange range);
        [PreserveSig] int RangeFromChild(IUIAutomationElement child, out IUIAutomationTextRange range);
        [PreserveSig] int GetSelection(out IUIAutomationTextRangeArray ranges);
        [PreserveSig] int GetVisibleRanges(out IUIAutomationTextRangeArray ranges);
        [PreserveSig] int get_DocumentRange(out IUIAutomationTextRange range);
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ILearningTextRange
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
        [PreserveSig] int Move(int unit, int count, out int moved);
        [PreserveSig] int MoveEndpointByUnit(int endpoint, int unit, int count, out int moved);
        [PreserveSig] int MoveEndpointByRange(int endpoint, IUIAutomationTextRange range, int otherEndpoint);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GuiThreadInfo
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    public sealed record LearningSnapshot(string Value, Utf16Range Selection);

    /// <summary>The one non-password element captured at paste. Use UIA members from a worker thread.</summary>
    public sealed class LearningTarget
    {
        readonly IUIAutomation uia;
        readonly IUIAutomationElement element;
        readonly int[] runtimeId;
        readonly uint pid;
        readonly nint window, nativeFocus;
        public uint ThreadId { get; }

        LearningTarget(IUIAutomation uia, IUIAutomationElement element, int[] runtimeId,
            uint pid, uint threadId, nint window, nint nativeFocus)
        {
            this.uia = uia;
            this.element = element;
            this.runtimeId = runtimeId;
            this.pid = pid;
            ThreadId = threadId;
            this.window = window;
            this.nativeFocus = nativeFocus;
        }

        static bool ExplicitlyNonPassword(IUIAutomationElement node) =>
            node.GetCurrentPropertyValueEx(IsPasswordProp, 1, out var value) == 0 && value is false;

        public static LearningTarget? Capture(out bool isPassword)
        {
            isPassword = false;
            try
            {
                var window = GetForegroundWindow();
                if (window == 0 || Automation.Value is not { } uia || uia.GetFocusedElement(out var element) != 0 || element is null) return null;
                isPassword = Prop<bool>(element, IsPasswordProp);
                var threadId = GetWindowThreadProcessId(window, out var pid);
                var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
                if (isPassword || !ExplicitlyNonPassword(element) || threadId == 0 || pid == (uint)Environment.ProcessId
                    || Prop<int>(element, ProcessIdProp) != (int)pid || !GetGUIThreadInfo(threadId, ref info) || info.Focus == 0
                    || element.GetRuntimeId(out var id) != 0 || id is not { Length: > 0 }) return null;
                var target = new LearningTarget(uia, element, id, pid, threadId, window, info.Focus);
                return target.IsCurrent() ? target : null;
            }
            catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException) { return null; }
        }

        // Cheap input-thread gate. The worker also checks UIA identity before decoding any key.
        public bool IsNativeFocusCurrent()
        {
            if (GetForegroundWindow() != window || GetWindowThreadProcessId(window, out var owner) != ThreadId || owner != pid) return false;
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            return GetGUIThreadInfo(ThreadId, ref info) && info.Focus == nativeFocus && (info.Flags & 0x1E) == 0;
        }

        public bool IsCurrent()
        {
            try
            {
                return IsNativeFocusCurrent() && uia.GetFocusedElement(out var focused) == 0 && focused is not null
                    && Prop<int>(focused, ProcessIdProp) == (int)pid && ExplicitlyNonPassword(focused)
                    && focused.GetRuntimeId(out var current) == 0 && current is not null && runtimeId.AsSpan().SequenceEqual(current)
                    && uia.CompareElements(element, focused, out var same) == 0 && same != 0;
            }
            catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException) { return false; }
        }

        public LearningSnapshot? Read()
        {
            const int start = 0, end = 1;
            try
            {
                if (!IsCurrent() || !ExplicitlyNonPassword(element)) return null;
                if (element.GetCurrentPattern(TextPatternId, out var provider) != 0 || provider is not ILearningTextPattern pattern
                    || pattern.GetSelection(out var ranges) != 0 || ranges is null || ranges.get_Length(out var count) != 0 || count != 1
                    || ranges.GetElement(0, out var selected) != 0 || selected is null
                    || pattern.get_DocumentRange(out var document) != 0 || document is not ILearningTextRange prefix) return null;

                // Use the provider's full value only when it uses the same coordinates as its text range.
                if (prefix.GetText(MaxText + 1, out var value) != 0 || value is null || value.Length > MaxText
                    || selected.GetText(MaxText + 1, out var selection) != 0 || selection is null
                    || prefix.MoveEndpointByRange(end, selected, start) != 0 || prefix.GetText(MaxText + 1, out var before) != 0
                    || before is null || before.Length > value.Length || selection.Length > value.Length - before.Length
                    || !value.AsSpan().StartsWith(before, StringComparison.Ordinal)
                    || !value.AsSpan(before.Length, selection.Length).SequenceEqual(selection) || !IsCurrent()) return null;
                return new LearningSnapshot(value, new Utf16Range(before.Length, selection.Length));
            }
            catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException) { return null; }
        }
    }
}
