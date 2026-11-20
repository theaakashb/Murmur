using Murmur.Core;
using Murmur.Platform;

namespace Murmur.Platform.Tests;

public sealed class VoiceEditBindingTests
{
    [Theory]
    [InlineData("custom:69", 0)]
    [InlineData("custom:69:5", 5)]
    public void SeparateChordOwnsItsRepeatAndRelease(string setting, int modifiers)
    {
        var binding = VoiceEditBinding.Parse(setting, KeyEvent.VkRControl);
        var latch = new EditShortcutCapture();
        var chord = new EditChord('E', modifiers);
        Assert.Equal(EditKeyAction.Press, latch.Handle(binding, chord, VoiceEditBinding.Modifiers, true, false, () => true));
        Assert.Equal(EditKeyAction.Swallow, latch.Handle(binding, chord, VoiceEditBinding.Modifiers, true, true, () => false));
        Assert.Equal(EditKeyAction.Release, latch.Handle(binding, chord with { Modifiers = 0 }, VoiceEditBinding.Modifiers, false, false, () => false));
    }

    [Theory]
    [InlineData("same", KeyEvent.VkRControl)]
    [InlineData("custom:69:16", KeyEvent.VkRControl)]
    [InlineData("custom:69:5", 'E')]
    [InlineData("custom:86:11", KeyEvent.VkRControl)]
    [InlineData("custom:27:0", KeyEvent.VkRControl)]
    public void InvalidOrConflictingEditBindingsAreInactive(string setting, int dictationKey) =>
        Assert.Null(VoiceEditBinding.Parse(setting, dictationKey));
}
