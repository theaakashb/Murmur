using Microsoft.Win32;

namespace Murmur.Platform;

/// <summary>Windows' microphone privacy switches, read only: the per-machine and per-user "microphone" consent values and the user's
/// "Let desktop apps access your microphone" value. Nothing is written; the user changes them in Settings → Privacy → Microphone.</summary>
public static class MicrophonePrivacy
{
    public enum State { Allowed, Blocked, Unknown }

    const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>Blocked when any of the three values says Deny; Allowed when at least one says Allow; otherwise Unknown.</summary>
    public static State Decide(string? machine, string? user, string? desktopApps)
    {
        string?[] values = [machine, user, desktopApps];
        if (values.Any(v => string.Equals(v, "Deny", StringComparison.OrdinalIgnoreCase))) return State.Blocked;
        return values.Any(v => string.Equals(v, "Allow", StringComparison.OrdinalIgnoreCase)) ? State.Allowed : State.Unknown;
    }

    public static State Read()
    {
        try
        {
            using var machine = Registry.LocalMachine.OpenSubKey(Store);
            using var user = Registry.CurrentUser.OpenSubKey(Store);
            using var desktop = Registry.CurrentUser.OpenSubKey(Store + @"\NonPackaged");
            return Decide(machine?.GetValue("Value") as string, user?.GetValue("Value") as string, desktop?.GetValue("Value") as string);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return State.Unknown; }
    }
}
