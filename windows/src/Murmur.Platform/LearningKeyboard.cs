using System.Runtime.InteropServices;
using System.Text;

namespace Murmur.Platform;

/// <summary>Key-to-character translation for the scoped keystroke fallback. Results go straight into the in-memory edit model; nothing is stored or logged.</summary>
public static class LearningKeyboard
{
    const uint DoNotChangeKeyState = 4; // ToUnicodeEx flag (Windows 10 1607+): dead keys and IME state in the target are left untouched
    const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkCapital = 0x14, VkLButton = 1, VkRButton = 2, VkMButton = 4, VkXButton1 = 5, VkXButton2 = 6;

    [DllImport("user32.dll")] static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer, int size, uint flags, nint layout);
    [DllImport("user32.dll")] static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    /// <summary>The text the key types under the held modifiers in the target thread's layout; null for a dead key or a key that types nothing.</summary>
    public static string? Translate(int vk, int scanCode, KeyModifiers modifiers, uint targetThread)
    {
        var state = new byte[256];
        if (modifiers.HasFlag(KeyModifiers.Shift)) state[VkShift] = 0x80;
        if (modifiers.HasFlag(KeyModifiers.Ctrl)) state[VkControl] = 0x80;
        if (modifiers.HasFlag(KeyModifiers.Alt)) state[VkMenu] = 0x80;
        if ((GetKeyState(VkCapital) & 1) != 0) state[VkCapital] = 1;
        var buffer = new StringBuilder(8);
        int n = ToUnicodeEx((uint)vk, (uint)scanCode, state, buffer, buffer.Capacity, DoNotChangeKeyState, GetKeyboardLayout(targetThread));
        return n > 0 ? buffer.ToString(0, n) : null;
    }

    /// <summary>True if a mouse button is down or was pressed since a previous check; a click moves the caret, which this model cannot follow.
    /// shortcut: GetAsyncKeyState's "pressed since last call" bit is shared with other callers, so a click can be missed; the next focus check is the backstop.</summary>
    public static bool MouseButtonUsed()
    {
        foreach (var button in new[] { VkLButton, VkRButton, VkMButton, VkXButton1, VkXButton2 }) if (GetAsyncKeyState(button) != 0) return true;
        return false;
    }
}
