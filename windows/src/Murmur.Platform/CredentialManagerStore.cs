using System.Runtime.InteropServices;
using System.Text;

namespace Murmur.Platform;

/// <summary>Content-free credential failure: carries the Win32 error code only, never a secret.</summary>
public sealed class CredentialStoreException(string message) : Exception(message);

/// <summary>
/// Windows Credential Manager generic credentials, target "&lt;prefix&gt;/&lt;account&gt;", persisted for this machine only
/// (CRED_PERSIST_LOCAL_MACHINE; never ENTERPRISE, which roams). Secrets are stored as UTF-8 and never logged.
/// </summary>
public sealed class CredentialManagerStore(string prefix = "Murmur")
{
    const uint CRED_TYPE_GENERIC = 1, CRED_PERSIST_LOCAL_MACHINE = 2;
    const int ERROR_NOT_FOUND = 1168, MaxBlob = 2560;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredReadW(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredEnumerateW(string filter, uint flags, out uint count, out nint credentials);
    [DllImport("advapi32.dll")] static extern void CredFree(nint buffer);

    string Target(string account)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Contains('*')) throw new ArgumentException("Invalid credential account", nameof(account));
        return $"{prefix}/{account}";
    }

    static CredentialStoreException Fail(string op, int error) => new($"Credential store {op} failed (Win32 error {error})");

    public bool Exists(string account) => Read(account) is not null;

    /// <summary>Null when no credential is stored for the account.</summary>
    public string? Read(string account)
    {
        if (!CredReadW(Target(account), CRED_TYPE_GENERIC, 0, out var ptr))
        {
            int error = Marshal.GetLastWin32Error();
            return error == ERROR_NOT_FOUND ? null : throw Fail("read", error);
        }
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            var bytes = new byte[c.CredentialBlobSize];
            if (bytes.Length > 0) Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(ptr); }
    }

    public void Save(string account, string secret)
    {
        var target = Target(account);
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length == 0 || bytes.Length > MaxBlob) { Array.Clear(bytes); throw new ArgumentException("Secret is empty or too long for Credential Manager", nameof(secret)); }
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL { Type = CRED_TYPE_GENERIC, TargetName = target, UserName = prefix, CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob, Persist = CRED_PERSIST_LOCAL_MACHINE };
            if (!CredWriteW(ref c, 0)) throw Fail("save", Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    /// <summary>A missing credential counts as deleted.</summary>
    public void Delete(string account)
    {
        if (!CredDeleteW(Target(account), CRED_TYPE_GENERIC, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ERROR_NOT_FOUND) throw Fail("delete", error);
        }
    }

    /// <summary>Removes every credential whose target starts with "&lt;prefix&gt;/".</summary>
    public void DeleteAll()
    {
        if (!CredEnumerateW($"{prefix}/*", 0, out var count, out var array))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND) return;
            throw Fail("enumerate", error);
        }
        var targets = new List<string>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var c = Marshal.PtrToStructure<CREDENTIAL>(Marshal.ReadIntPtr(array, i * nint.Size));
                if (c.Type == CRED_TYPE_GENERIC && c.TargetName is { } t && t.StartsWith(prefix + "/", StringComparison.Ordinal)) targets.Add(t);
            }
        }
        finally { CredFree(array); }
        foreach (var t in targets)
            if (!CredDeleteW(t, CRED_TYPE_GENERIC, 0) && Marshal.GetLastWin32Error() is var e && e != ERROR_NOT_FOUND) throw Fail("delete", e);
    }
}
