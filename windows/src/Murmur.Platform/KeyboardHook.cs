using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Murmur.Platform;

[Flags]
public enum KeyModifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Win = 8 }

/// <summary>One low-level keyboard event. Only codes and flags: never turned into text, stored or logged.</summary>
public readonly record struct KeyEvent(int VirtualKey, int ScanCode, bool IsDown, bool Extended, bool Injected, uint Time, long Timestamp, KeyModifiers Modifiers)
{
    public const int VkEscape = 0x1B, VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5, VkLWin = 0x5B, VkRWin = 0x5C;
    public bool IsRightControl => VirtualKey == VkRControl;
}

/// <summary>A key plus the exact modifiers that must be held with it (default paste-last: Ctrl+Win+Shift+V).</summary>
public readonly record struct Chord(int Key, KeyModifiers Modifiers)
{
    public static readonly Chord PasteLast = new('V', KeyModifiers.Ctrl | KeyModifiers.Win | KeyModifiers.Shift);
    public bool Matches(in KeyEvent e) => e.IsDown && e.VirtualKey == Key && e.Modifiers == Modifiers;
}

/// <summary>
/// WH_KEYBOARD_LL on a dedicated thread with its own message loop, so a busy UI thread never delays it.
/// The handler runs on that thread and must return quickly (Windows drops slow hooks); returning true swallows the key.
/// Injected events (SendInput, including our own Ctrl+V) are not passed to the handler unless asked for.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    public delegate bool KeyHandler(in KeyEvent e);

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_QUIT = 0x12;
    const uint LLKHF_EXTENDED = 1, LLKHF_LOWER_IL_INJECTED = 2, LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x, y; }
    delegate nint HookProc(int code, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookExW(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PostThreadMessageW(uint thread, uint msg, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);

    readonly KeyHandler handler;
    readonly bool ignoreInjected;
    readonly HookProc proc; // kept as a field so the GC cannot collect the native callback
    readonly Thread thread;
    readonly ManualResetEventSlim ready = new();
    nint hook;
    uint threadId;
    int pressed; // bit per physical modifier key, written only on the hook thread
    Exception? startError;
    bool disposed;

    /// <summary>Modifiers physically held as of the last event the hook saw.</summary>
    public KeyModifiers Modifiers => ModifiersOf(Volatile.Read(ref pressed));

    public KeyboardHook(KeyHandler handler, bool ignoreInjected = true)
    {
        this.handler = handler;
        this.ignoreInjected = ignoreInjected;
        proc = OnKey;
        thread = new Thread(Run) { IsBackground = true, Name = "Murmur keyboard hook" };
        thread.Start();
        ready.Wait();
        if (startError is not null) throw startError;
    }

    void Run()
    {
        threadId = GetCurrentThreadId();
        hook = SetWindowsHookExW(WH_KEYBOARD_LL, proc, GetModuleHandleW(null), 0);
        if (hook == 0) startError = new InvalidOperationException($"Keyboard hook could not be installed (Win32 error {Marshal.GetLastWin32Error()})");
        ready.Set();
        if (hook == 0) return;
        while (GetMessageW(out _, 0, 0, 0) > 0) { } // the hook runs inside GetMessage; WM_QUIT ends it
        UnhookWindowsHookEx(hook);
        hook = 0;
    }

    static int BitOf(int vk) => vk switch
    {
        KeyEvent.VkLShift => 1, KeyEvent.VkRShift => 2, KeyEvent.VkLControl => 4, KeyEvent.VkRControl => 8,
        KeyEvent.VkLMenu => 16, KeyEvent.VkRMenu => 32, KeyEvent.VkLWin => 64, KeyEvent.VkRWin => 128, _ => 0,
    };

    static KeyModifiers ModifiersOf(int bits) =>
        ((bits & 0b1100) != 0 ? KeyModifiers.Ctrl : 0) | ((bits & 0b11) != 0 ? KeyModifiers.Shift : 0)
        | ((bits & 0b110000) != 0 ? KeyModifiers.Alt : 0) | ((bits & 0b11000000) != 0 ? KeyModifiers.Win : 0);

    internal nint OnKey(int code, nuint wParam, nint lParam)
    {
        if (code < 0) return CallNextHookEx(hook, code, wParam, lParam);
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;
            if (!(injected && ignoreInjected))
            {
                bool down = wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
                int vk = (int)k.vkCode, bit = BitOf(vk), bits = pressed;
                if (bit != 0) { bits = down ? bits | bit : bits & ~bit; Volatile.Write(ref pressed, bits); }
                var e = new KeyEvent(vk, (int)k.scanCode, down, (k.flags & LLKHF_EXTENDED) != 0, injected, k.time, Stopwatch.GetTimestamp(), ModifiersOf(bits));
                if (handler(e)) return 1;
            }
        }
        catch { /* a failing handler must never break the system keyboard */ }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (hook != 0) PostThreadMessageW(threadId, WM_QUIT, 0, 0);
        thread.Join(2000);
    }
}
