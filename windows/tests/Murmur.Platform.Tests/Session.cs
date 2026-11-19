using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// A test that needs the logged-in desktop (SendInput, other apps' UI Automation, focus). Skipped, with a reason, when the test process is not
/// in the interactive console session (for example over SSH). Run them in session 1 with windows/tests/Murmur.Platform.Tests/run-session1.cmd.
/// </summary>
public sealed class SessionFactAttribute : FactAttribute
{
    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] static extern bool ProcessIdToSessionId(uint pid, out uint session);

    public static bool Interactive =>
        OperatingSystem.IsWindows() && ProcessIdToSessionId((uint)Environment.ProcessId, out var mine) && mine != 0 && mine == WTSGetActiveConsoleSessionId();

    public SessionFactAttribute()
    {
        if (!Interactive) Skip = "Needs the interactive desktop session (not SSH); run windows/tests/Murmur.Platform.Tests/run-session1.cmd";
    }
}

/// <summary>Needs the desktop and a desktop that accepts input: a locked or disconnected console silently drops SendInput and SetCursorPos.</summary>
public sealed class InputFactAttribute : FactAttribute
{
    [DllImport("user32.dll")] static extern bool GetCursorPos(out long point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

    public InputFactAttribute()
    {
        if (!SessionFactAttribute.Interactive) Skip = "Needs the interactive desktop session (not SSH); run windows/tests/Murmur.Platform.Tests/run-session1.cmd";
        else if (!(GetCursorPos(out var p) && SetCursorPos((int)(p & 0xFFFFFFFF), (int)(p >> 32)))) Skip = "The desktop does not accept input (locked, or console not attached to a display)";
    }
}

/// <summary>Opens Notepad and waits until it is the foreground window's process.</summary>
sealed class NotepadWindow : IDisposable
{
    readonly Process process;
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    public NotepadWindow()
    {
        process = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
        for (int i = 0; i < 100; i++)
        {
            Thread.Sleep(100);
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (Process.GetProcessById((int)pid).ProcessName.Equals("notepad", StringComparison.OrdinalIgnoreCase)) { Thread.Sleep(500); return; }
        }
        throw new InvalidOperationException("Notepad did not become the foreground window");
    }

    public void Dispose()
    {
        foreach (var p in Process.GetProcessesByName("notepad")) { try { p.Kill(); } catch (Exception) { } }
        process.Dispose();
    }
}
