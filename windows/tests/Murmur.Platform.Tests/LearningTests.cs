using System.Diagnostics;
using System.Runtime.InteropServices;
using Murmur.Core;
using Murmur.Platform;
using Xunit.Abstractions;

namespace Murmur.Platform.Tests;

/// <summary>Learning reader and keystroke translation against real windows (synthetic text only). Needs the interactive desktop.</summary>
[Collection("Browser desktop")]
public sealed class LearningTests(ITestOutputHelper output)
{
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    static void Tap(byte vk, bool shift = false)
    {
        if (shift) keybd_event(0x10, 0, 0, 0);
        keybd_event(vk, 0, 0, 0); keybd_event(vk, 0, 2, 0);
        if (shift) keybd_event(0x10, 0, 2, 0);
        Thread.Sleep(30);
    }

    static void Type(string text)
    {
        foreach (var c in text) Tap(c == ' ' ? (byte)0x20 : (byte)char.ToUpperInvariant(c), shift: char.IsUpper(c));
        Thread.Sleep(200);
    }

    [InputFact]
    public void ReadableFieldGivesTheSpanOffsetAndTheEditedText()
    {
        using var notepad = new NotepadWindow();
        Type("Start ");
        var target = FocusProbe.LearningTarget.Capture(out bool password);
        Assert.False(password);
        Assert.NotNull(target);
        var before = target!.Read();
        Assert.NotNull(before);
        // Notepad may restore earlier text, so the span's offset is wherever the caret is.
        int at = before!.Selection.Location;
        Assert.True(at >= 6 && before.Value[(at - 6)..at] == "Start ", "caret not after the typed prefix");
        Type("git hub rocks");   // stands in for the pasted span
        var after = target.Read();
        Assert.NotNull(after);
        Assert.Equal(before.Value.Insert(at, "git hub rocks"), after!.Value);
        var observation = DictationObservation.Create(after.Value, new Utf16Range(at, 13), 0);
        Assert.NotNull(observation);
        // The user fixes "git hub" -> "GitHub": caret is at the span end; go back 6 words-worth by Ctrl+Left twice then retype.
        for (int i = 0; i < 6; i++) Tap(0x25); // Left x6 puts the caret before " rocks"
        for (int i = 0; i < 7; i++) Tap(0x08);  // Backspace "git hub"
        Type("GitHub");
        var edited = target.Read();
        Assert.NotNull(edited);
        observation!.Observe(edited!.Value, edited.Selection, 1, sameFocusedField: true, readableNonsecure: true);
        output.WriteLine($"stopped={observation.Stopped} editedLength={observation.Edited?.Length}");
        Assert.Equal("GitHub rocks", observation.Edited);
    }

    [InputFact] // needs the test window in the foreground; hosted CI can't reliably foreground it (the 0.3.6 release run failed twice here)
    public void PasswordFieldIsNeverCaptured()
    {
        using var window = ScriptWindow.Open("$t = New-Object Windows.Forms.TextBox; $t.UseSystemPasswordChar = $true; $t.Text = 'synthetic-secret'; $t.Dock = 'Fill'; $f.Controls.Add($t); $f.Add_Shown({ $t.Focus() })");
        var target = FocusProbe.LearningTarget.Capture(out bool password);
        output.WriteLine($"password={password} target={(target is null ? "null" : "set")}");
        Assert.Null(target);
        Assert.True(password);
    }

    [InputFact] // injects keys into a foreground test window; hosted CI can't reliably foreground it
    public void UnreadableFieldIsCapturedButNotReadAndKeysAreFollowedByTheHook()
    {
        using var window = ScriptWindow.Open("$b = New-Object Windows.Forms.Button; $b.Text = 'x'; $b.Dock = 'Fill'; $f.Controls.Add($b); $f.Add_Shown({ $b.Focus() })");
        var target = FocusProbe.LearningTarget.Capture(out _);
        Assert.NotNull(target);
        Assert.Null(target!.Read());
        var span = new KeystrokeSpan("call acme now") { Resolve = _ => null };
        using var hook = new KeyboardHook((in KeyEvent e) =>
        {
            if (!e.IsDown || LearningKeys.IsModifier(e.VirtualKey) || !target.IsNativeFocusCurrent()) return false;
            var k = e;
            span.Apply(LearningKeys.Map(e.VirtualKey, e.Modifiers.HasFlag(KeyModifiers.Ctrl), e.Modifiers.HasFlag(KeyModifiers.Shift), e.Modifiers.HasFlag(KeyModifiers.Alt),
                e.Modifiers.HasFlag(KeyModifiers.Win), () => LearningKeyboard.Translate(k.VirtualKey, k.ScanCode, k.Modifiers, target.ThreadId)));
            return false;
        }, ignoreInjected: false);
        for (int i = 0; i < 3; i++) Tap(0x08);
        Type("NOw");
        Thread.Sleep(300);
        output.WriteLine($"state={span.State} length={span.Value.Length}");
        Assert.Equal("call acme NOw", span.Value);
        Tap(0x41, shift: false); // fine: more typing still tracked
        Tap(0x5A);
        Thread.Sleep(200);
        Assert.Equal("call acme NOwaz", span.Value);
        // Ctrl+Z is a shortcut: the evidence is dropped.
        keybd_event(0x11, 0, 0, 0); Tap(0x5A); keybd_event(0x11, 0, 2, 0);
        Thread.Sleep(200);
        Assert.Equal(KeystrokeSpan.SpanState.Lost, span.State);
    }

    /// <summary>A WinForms window in a separate PowerShell process (the reader rejects Murmur's own process), foreground and focused.</summary>
    sealed class ScriptWindow : IDisposable
    {
        readonly Process process;
        ScriptWindow(Process p) => process = p;

        public static ScriptWindow Open(string controls)
        {
            var script = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object Windows.Forms.Form; $f.Width = 300; $f.Height = 150; $f.TopMost = $true; " +
                controls + "; $f.Add_Shown({ $f.Activate() }); [void]$f.ShowDialog()";
            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-STA"); info.ArgumentList.Add("-EncodedCommand");
            info.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
            var p = Process.Start(info)!;
            for (int i = 0; i < 100; i++)
            {
                Thread.Sleep(100);
                GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
                if (pid == p.Id) { Thread.Sleep(500); return new ScriptWindow(p); }
            }
            try { p.Kill(true); } catch (Exception) { }
            throw new InvalidOperationException("The test window did not become the foreground window");
        }

        public void Dispose() { try { process.Kill(true); } catch (Exception) { } process.Dispose(); }
    }
}
