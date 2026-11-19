// Port of app/PrivateFiles.swift.
namespace Murmur.Core;

/// <summary>
/// Owner-only private JSON files with atomic replacement. The containing directory is 0700 and each file 0600 on Unix;
/// the file is written to a temporary file in the same folder, then moved over the target.
/// </summary>
public static class PrivateFiles
{
    const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode FileUnixMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Windows: a protected DACL (no inheritance from parents) granting full control to the current user and SYSTEM only.
    /// On a folder the entries are inheritable, so files created inside inherit them; a file gets the same two entries directly.
    /// </summary>
    static void HardenWindowsAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User ?? throw new StoreError("Cannot determine the current user");
        var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        var folder = Directory.Exists(path);
        var inherit = folder ? System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit : System.Security.AccessControl.InheritanceFlags.None;
        void Fill(System.Security.AccessControl.FileSystemSecurity security)
        {
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { user, system })
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl, inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        }
        if (folder) { var security = new System.Security.AccessControl.DirectorySecurity(); Fill(security); new DirectoryInfo(path).SetAccessControl(security); }
        else { var security = new System.Security.AccessControl.FileSecurity(); Fill(security); new FileInfo(path).SetAccessControl(security); }
    }

    static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    public static void PrepareDirectory(string path)
    {
        if (File.Exists(path) || (Directory.Exists(path) && IsLink(path))) throw new StoreError("Private storage requires a real directory");
        if (!Directory.Exists(path))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else Directory.CreateDirectory(path, DirectoryMode);
        }
        if (OperatingSystem.IsWindows()) HardenWindowsAcl(path); else File.SetUnixFileMode(path, DirectoryMode);
    }

    public static void Write(byte[] data, string path)
    {
        if (Directory.Exists(path) || (File.Exists(path) && IsLink(path))) throw new StoreError("Private file is not a regular file");
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileUnixMode; // never readable by others, even briefly
            using (var stream = new FileStream(temp, options)) { stream.Write(data); stream.Flush(true); }
            File.Move(temp, path, overwrite: true);
            if (OperatingSystem.IsWindows()) HardenWindowsAcl(path); else File.SetUnixFileMode(path, FileUnixMode);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Null when the file does not exist yet.</summary>
    public static byte[]? Read(string path)
    {
        if (Directory.Exists(path) || (File.Exists(path) && IsLink(path))) throw new StoreError("Private file is not a regular file");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
