using Murmur.Platform;

namespace Murmur.App;

/// <summary>The stored <c>shortcut</c> value: a preset name or <c>custom:&lt;virtual-key&gt;</c> (decimal). Anything unreadable is Right Ctrl.</summary>
internal static class ShortcutSpec
{
    public const string RightCtrl = "right-ctrl", RightAlt = "right-alt", RightShift = "right-shift";
    public static readonly string[] Presets = [RightCtrl, RightAlt, RightShift];

    public static int KeyOf(string spec) => spec switch
    {
        RightAlt => KeyEvent.VkRMenu,
        RightShift => KeyEvent.VkRShift,
        _ when spec.StartsWith("custom:", StringComparison.Ordinal) && int.TryParse(spec.AsSpan(7), out var vk) && vk is > 0 and < 256 && vk != KeyEvent.VkEscape => vk,
        _ => KeyEvent.VkRControl,
    };

    public static string Custom(int vk) => "custom:" + vk;

    public static string Label(string spec) => spec switch
    {
        RightCtrl => "Right Ctrl",
        RightAlt => "Right Alt",
        RightShift => "Right Shift",
        _ => KeyOf(spec) == KeyEvent.VkRControl && !spec.StartsWith("custom:", StringComparison.Ordinal) ? "Right Ctrl" : Native.KeyName(KeyOf(spec)),
    };

    /// <summary>Modifier keys are not swallowed (they must keep working for the app underneath); a custom key is.</summary>
    public static bool IsModifier(int vk) => vk is >= 0xA0 and <= 0xA5 or 0x5B or 0x5C or 0x10 or 0x11 or 0x12;
}
