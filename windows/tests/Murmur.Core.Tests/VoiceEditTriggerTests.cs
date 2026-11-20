// Ports app/check_voice_edit_trigger.swift. Key codes are Windows virtual keys; the chord model is EditChord (key code + modifier bits).
using Murmur.Core;

namespace Murmur.Core.Tests;

public class VoiceEditTriggerTests
{
    const int Ctrl = 2, Alt = 4, Win = 8, Relevant = Ctrl | Alt | Win | 1;
    const int E = 0x45, R = 0x52, D = 0x44, Z = 0x5A, V = 0x56, C = 0x43, Space = 0x20, RightAlt = 0xA5;

    [Fact]
    public void SelectionRule()
    {
        Assert.True(EditTrigger.CapturesKey(false, "some words"));
        Assert.False(EditTrigger.CapturesKey(false, null));
        Assert.False(EditTrigger.CapturesKey(false, ""));
        Assert.False(EditTrigger.CapturesKey(false, " \n\t"));
        Assert.False(EditTrigger.CapturesKey(true, "some words"));
    }

    [Fact]
    public void CtrlEKeepsWorkingWithoutASelection()
    {
        var binding = new EditChord(E, Ctrl);
        bool selected = false;
        var capture = new EditShortcutCapture();
        EditKeyAction Press(int key, int mods, bool down, bool repeat = false) =>
            capture.Handle(binding, new(key, mods), Relevant, down, repeat, () => EditTrigger.CapturesKey(false, selected ? "text" : null));

        Assert.Equal(EditKeyAction.Pass, Press(E, Ctrl, true));
        Assert.Equal(EditKeyAction.Pass, Press(E, Ctrl, false));

        selected = true;
        Assert.Equal(EditKeyAction.Press, Press(E, Ctrl, true));
        Assert.Equal(EditKeyAction.Swallow, Press(E, Ctrl, true, repeat: true));
        Assert.Equal(EditKeyAction.Release, Press(E, 0, false)); // released even after Control is let go
        Assert.Equal(EditKeyAction.Pass, Press(E, 0, false));

        Assert.Equal(EditKeyAction.Pass, Press(E, Ctrl | Alt, true));
        Assert.Equal(EditKeyAction.Pass, Press(R, Ctrl, true));

        selected = false;
        Assert.Equal(EditKeyAction.Pass, Press(E, Ctrl, true));
        capture.Reset();
        Assert.Equal(EditKeyAction.Pass, capture.Handle(binding, new(E, Ctrl), Relevant, true, false, () => EditTrigger.CapturesKey(true, "text")));
    }

    static string? Refusal(int key, int mods, EditChord? dictation) => EditShortcut.EditRefusal(new(key, mods), dictation, Relevant,
        c => c.Modifiers == 0 ? "Pick a key with a modifier." : null,
        new Dictionary<EditChord, string> { [new(Z, Ctrl | Win)] = "Undo", [new(V, Ctrl | Win)] = "Paste", [new(C, Ctrl | Win)] = "Copy" });

    [Fact]
    public void WhichKeysCanBeTheSeparateShortcut()
    {
        var dictation = new EditChord(D, Ctrl | Alt);
        Assert.Null(Refusal(E, Ctrl, dictation));
        Assert.NotNull(Refusal(D, Ctrl | Alt, dictation));
        Assert.NotNull(Refusal(Space, Ctrl | Alt, new EditChord(Space, Ctrl | Alt)));
        Assert.NotNull(Refusal(Z, Ctrl | Win, dictation));
        Assert.True(Refusal(V, Ctrl | Win, dictation) is not null && Refusal(C, Ctrl | Win, dictation) is not null);
        Assert.True(Refusal(RightAlt, 0, dictation) is not null && Refusal(E, 0, dictation) is not null);
    }

    [Fact]
    public void DictationShortcutCannotCollideWithVoiceEdit()
    {
        var edit = new EditChord(E, Ctrl);
        Assert.NotNull(VoiceEditSettings.VoiceEditConflict("ctrl-e", edit, _ => edit, Relevant));
        Assert.Null(VoiceEditSettings.VoiceEditConflict("ctrl-r", new(R, Ctrl), _ => edit, Relevant));
        Assert.Null(VoiceEditSettings.VoiceEditConflict(EditTrigger.SameKey, edit, _ => edit, Relevant));
    }
}
