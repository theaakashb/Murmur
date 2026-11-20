using Murmur.Core;

namespace Murmur.Platform;

public static class VoiceEditBinding
{
    public const int Modifiers = (int)(KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Win);

    public static EditChord? Parse(string spec, int dictationKey)
    {
        var parts = spec.Split(':');
        if (parts.Length is < 2 or > 3 || parts[0] != "custom" ||
            !int.TryParse(parts[1], out int key) || key is <= 0 or >= 255 || key == dictationKey ||
            key is KeyEvent.VkEscape or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5)
            return null;

        int modifiers = 0;
        if (parts.Length == 3 && (!int.TryParse(parts[2], out modifiers) || (modifiers & ~Modifiers) != 0))
            return null;
        if (key == Chord.PasteLast.Key && modifiers == (int)Chord.PasteLast.Modifiers) return null;
        return new EditChord(key, modifiers);
    }
}
