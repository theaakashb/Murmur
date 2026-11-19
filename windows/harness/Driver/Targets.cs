using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace Murmur.Harness.Driver;

/// <summary>A target app the dictations are pasted into. Text is read back through UI Automation.</summary>
abstract class Target
{
    public abstract string Name { get; }
    protected string Dir = "";
    protected AutomationElement? Window, Editor;
    protected int Pid;
    public string ReadMethod = "";

    /// <summary>Launches the app and focuses an empty editor.</summary>
    public void Start(string workDir)
    {
        Dir = Path.Combine(workDir, Name); Directory.CreateDirectory(Dir);
        Launch();
        Window = Wait(() => FindWindow(), 30) ?? throw new Exception($"{Name}: window not found");
        Pid = Window.Current.ProcessId;
        Thread.Sleep(1500);
        Focus();
        Prepare();
        Editor = Wait(() => FindEditor(Window), 20);
        if (Editor is null) { Dump(); throw new Exception($"{Name}: editor element not found"); }
        Focus();
    }
    protected abstract void Launch();
    protected abstract AutomationElement? FindWindow();
    protected abstract AutomationElement? FindEditor(AutomationElement window);
    protected virtual void Prepare() { }

    public IntPtr Hwnd => new IntPtr(Window!.Current.NativeWindowHandle);
    public bool Focus()
    {
        var ok = Native.Focus(Hwnd);
        if (!ok && Editor is not null)
        {
            // Last resort: click the middle of the window (activates it by real input; the editor is re-focused through UIA below).
            try { var r = Window!.Current.BoundingRectangle; Native.Click((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)); } catch (Exception) { }
            Thread.Sleep(300); ok = Native.GetForegroundWindow() == Hwnd;
        }
        try { Editor?.SetFocus(); } catch (Exception) { /* some editors refuse; the window focus stays */ }
        return ok;
    }

    /// <summary>Clears the editor with Ctrl+A, Delete. Returns true when it reads back empty.</summary>
    public bool Clear()
    {
        Focus(); Thread.Sleep(150);
        Native.Chord(Native.VK_CONTROL, 'A'); Thread.Sleep(150); Native.Tap(Native.VK_DELETE);
        return Wait(() => Read()?.Trim().Length == 0 ? "" : null, 3) is not null;
    }

    /// <summary>UI Automation read-back: ValuePattern, else TextPattern. Null when the editor exposes neither.</summary>
    public string? Read()
    {
        if (Editor is null) return null;
        try
        {
            // ValuePattern first: Chromium's TextPattern reports the aria-label for an empty field.
            if (Editor.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) { ReadMethod = "UIA ValuePattern"; return ((ValuePattern)vp).Current.Value; }
            if (Editor.TryGetCurrentPattern(TextPattern.Pattern, out var tp)) { ReadMethod = "UIA TextPattern"; return ((TextPattern)tp).DocumentRange.GetText(-1); }
        }
        catch (Exception) { /* element went stale or the app is busy: caller retries */ }
        return null;
    }

    public void Screenshot(string path)
    {
        Focus(); Thread.Sleep(400);
        var r = Window!.Current.BoundingRectangle;
        using var bmp = new System.Drawing.Bitmap((int)r.Width, (int)r.Height);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen((int)r.X, (int)r.Y, 0, 0, bmp.Size);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    public void Dump()
    {
        foreach (System.Windows.Automation.AutomationElement e in Window!.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
        {
            var c = e.Current; var ct = c.ControlType.ProgrammaticName;
            if (ct is not ("ControlType.Edit" or "ControlType.Document") && !Reads(e)) continue;
            Console.WriteLine($"{ct} name='{c.Name}' id='{c.AutomationId}' class='{c.ClassName}' text={e.TryGetCurrentPattern(TextPattern.Pattern, out _)} value={e.TryGetCurrentPattern(ValuePattern.Pattern, out _)} focus={c.HasKeyboardFocus}");
        }
    }

    public virtual void Close()
    {
        try { if (Pid != 0) Process.GetProcessById(Pid).Kill(true); } catch (Exception) { /* already gone */ }
    }

    // ---- helpers ----
    public static T? Wait<T>(Func<T?> f, int seconds) where T : class
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        do { try { var v = f(); if (v is not null) return v; } catch (ElementNotAvailableException) { } Thread.Sleep(250); } while (DateTime.UtcNow < end);
        return null;
    }
    protected static AutomationElement? TopWindow(Func<string, bool> name) =>
        AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>()
            .FirstOrDefault(w => name(w.Current.Name ?? ""));
    protected static bool Reads(AutomationElement e) =>
        e.TryGetCurrentPattern(TextPattern.Pattern, out _) || e.TryGetCurrentPattern(ValuePattern.Pattern, out _);
    protected static AutomationElement? Editable(AutomationElement w, Func<AutomationElement, bool> pick) =>
        w.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)))
         .Cast<AutomationElement>().FirstOrDefault(e => pick(e) && Reads(e));
}

sealed class NotepadTarget : Target
{
    public override string Name => "notepad";
    string file = "";
    protected override void Launch()
    {
        file = Path.Combine(Dir, "harness-note.txt"); File.WriteAllText(file, "");
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
    }
    protected override AutomationElement? FindWindow() => TopWindow(n => n.Contains("harness-note"));
    protected override AutomationElement? FindEditor(AutomationElement w) => Editable(w, _ => true);
}

sealed class EdgeTarget : Target
{
    public override string Name => "edge";
    public string PageUrl = "";
    protected override void Launch()
    {
        var exe = new[] { @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe" }.First(File.Exists);
        Process.Start(new ProcessStartInfo(exe, $"--user-data-dir=\"{Path.Combine(Dir, "profile")}\" --no-first-run --no-default-browser-check --disable-extensions --disable-sync --force-renderer-accessibility --new-window \"{PageUrl}\"") { UseShellExecute = false });
    }
    protected override AutomationElement? FindWindow() => TopWindow(n => n.Contains("Murmur harness page"));
    protected override AutomationElement? FindEditor(AutomationElement w) => Editable(w, e => e.Current.Name == "dictation" && e.Current.ControlType == ControlType.Edit);
}

sealed class CodeTarget : Target
{
    public override string Name => "code";
    protected override void Launch()
    {
        var exe = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, @"Programs\Microsoft VS Code\Code.exe");
        var user = Path.Combine(Dir, "data", "User"); Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "settings.json"),
            "{\"editor.accessibilitySupport\":\"on\",\"editor.experimentalEditContextEnabled\":false,\"workbench.startupEditor\":\"none\",\"telemetry.telemetryLevel\":\"off\",\"update.mode\":\"none\"," +
            "\"window.restoreWindows\":\"none\",\"security.workspace.trust.enabled\":false,\"workbench.tips.enabled\":false,\"editor.wordWrap\":\"on\"," +
            "\"editor.quickSuggestions\":{\"other\":false,\"comments\":false,\"strings\":false},\"editor.suggestOnTriggerCharacters\":false,\"editor.acceptSuggestionOnEnter\":\"off\"}");
        Process.Start(new ProcessStartInfo(exe, $"--user-data-dir=\"{Path.Combine(Dir, "data")}\" --extensions-dir=\"{Path.Combine(Dir, "ext")}\" --disable-extensions --disable-gpu --new-window --force-renderer-accessibility") { UseShellExecute = true });
    }
    protected override AutomationElement? FindWindow() => TopWindow(n => n.EndsWith("Visual Studio Code"));
    protected override void Prepare() { Native.Chord(Native.VK_CONTROL, 'N'); Thread.Sleep(1500); } // new untitled file
    protected override AutomationElement? FindEditor(AutomationElement w) =>
        Editable(w, e => e.Current.ClassName == "native-edit-context" || (e.Current.Name ?? "").Contains("ditor", StringComparison.OrdinalIgnoreCase));
}
