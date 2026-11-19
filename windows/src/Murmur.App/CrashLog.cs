namespace Murmur.App;

/// <summary>A startup crash leaves the exception type, HRESULT and stack in %TEMP%\Murmur-crash.txt. Content-free: no transcript, settings values or keys.</summary>
internal static class CrashLog
{
    public static void Write(Exception e)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "Murmur-crash.txt"), $"{DateTime.UtcNow:o} {e.GetType().FullName} 0x{e.HResult:X8}\n{e.StackTrace}\n{(e.InnerException is { } i ? $"inner {i.GetType().FullName} 0x{i.HResult:X8}\n{i.StackTrace}\n" : "")}\n"); }
        catch (Exception) { }
    }
}
