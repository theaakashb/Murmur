namespace Murmur.Core;

/// <summary>Turns one physical key press into the edit it makes to the dictated span (<see cref="KeystrokeSpan.Key"/>). Pure: the caller supplies
/// the virtual key, the modifiers held and a translator for the key's character, so the mapping is testable off Windows. Nothing here stores keys.
/// The Windows counterpart of <c>AppController.watchedKey</c>.</summary>
public static class LearningKeys
{
    public const int VkBack = 0x08, VkTab = 0x09, VkReturn = 0x0D, VkEscape = 0x1B, VkPrior = 0x21, VkNext = 0x22, VkEnd = 0x23, VkHome = 0x24,
        VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28, VkDelete = 0x2E, VkProcessKey = 0xE5;

    /// <param name="translate">The character(s) this key types under the held modifiers and the target's layout; null for a dead key or a key that types nothing.
    /// Called only for plain typing keys, never for navigation, so a hook never translates a key the model ignores.</param>
    public static KeystrokeSpan.Key Map(int vk, bool ctrl, bool shift, bool alt, bool win, Func<string?> translate)
    {
        // Win chords, IME composition (VK_PROCESSKEY) and Ctrl/Alt shortcuts (including undo) are not modelled: the evidence is dropped.
        if (win || vk == VkProcessKey) return new KeystrokeSpan.Key.Other();
        switch (vk)
        {
            case VkBack: return alt ? new KeystrokeSpan.Key.Other() : new KeystrokeSpan.Key.Backspace(ctrl);
            case VkDelete: return ctrl || alt || shift ? new KeystrokeSpan.Key.Other() : new KeystrokeSpan.Key.ForwardDelete();
            case VkLeft: return alt ? new KeystrokeSpan.Key.Other() : new KeystrokeSpan.Key.Left(ctrl, shift);
            case VkRight: return alt ? new KeystrokeSpan.Key.Other() : new KeystrokeSpan.Key.Right(ctrl, shift);
            // Return, Tab, Escape and vertical or page caret keys leave the span without undoing it, like focus departure.
            case VkReturn or VkTab or VkEscape or VkUp or VkDown or VkHome or VkEnd or VkPrior or VkNext: return new KeystrokeSpan.Key.Text("\n");
        }
        // Ctrl or Alt alone makes a shortcut; both together is AltGr on many layouts and still types a character.
        if (ctrl != alt) return new KeystrokeSpan.Key.Other();
        return translate() is { Length: > 0 } text ? new KeystrokeSpan.Key.Text(text) : new KeystrokeSpan.Key.Other();
    }

    /// <summary>The modifier and lock keys, which change no text by themselves.</summary>
    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x14 or 0x90 or 0x91 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C or 0x5D;
}
