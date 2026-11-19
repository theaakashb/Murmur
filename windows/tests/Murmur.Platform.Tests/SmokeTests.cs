using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.Platform.Tests;

public class SmokeTests
{
    [Fact]
    public void RunsOnWindows() => Assert.True(OperatingSystem.IsWindows());
}

public class CredentialManagerStoreTests
{
    // Credential Manager calls fail intermittently with ERROR_NO_SUCH_LOGON_SESSION (1312) in a network logon such as SSH, so these need the desktop session.
    [SessionFact]
    public void SaveReadDeleteRoundTrip()
    {
        var prefix = "MurmurTest/" + Guid.NewGuid().ToString("N");
        var store = new CredentialManagerStore(prefix);
        try
        {
            Assert.Null(store.Read("a"));
            Assert.False(store.Exists("a"));
            store.Save("a", "synthetic-secret-é中");
            store.Save("b", "second");
            Assert.Equal("synthetic-secret-é中", store.Read("a"));
            store.Save("a", "replaced");
            Assert.Equal("replaced", store.Read("a"));
            Assert.True(store.Exists("a"));
            store.Delete("a");
            store.Delete("a"); // missing is success
            Assert.Null(store.Read("a"));
            Assert.Equal("second", store.Read("b"));
        }
        finally { store.Delete("a"); store.Delete("b"); }
    }

    [SessionFact]
    public void DeleteAllRemovesEveryCredentialUnderThePrefixOnly()
    {
        var prefix = "MurmurTest/" + Guid.NewGuid().ToString("N");
        var store = new CredentialManagerStore(prefix);
        var other = new CredentialManagerStore(prefix + "x");
        try
        {
            store.Save("a", "1"); store.Save("b", "2"); other.Save("a", "3");
            store.DeleteAll();
            Assert.Null(store.Read("a"));
            Assert.Null(store.Read("b"));
            Assert.Equal("3", other.Read("a"));
        }
        finally { store.Delete("a"); store.Delete("b"); other.Delete("a"); }
    }

    [Fact]
    public void RejectsWildcardAndEmptyAccounts()
    {
        var store = new CredentialManagerStore("MurmurTest/" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentException>(() => store.Read("*"));
        Assert.Throws<ArgumentException>(() => store.Save("", "x"));
        Assert.Throws<ArgumentException>(() => store.Save("a", ""));
    }
}

public class PrivateFilesAclTests
{
    static readonly SecurityIdentifier System_ = new(WellKnownSidType.LocalSystemSid, null);

    static void AssertOwnerAndSystemOnly(FileSystemSecurity security, bool inheritedAllowed)
    {
        Assert.True(security.AreAccessRulesProtected, "DACL must not inherit from the parent");
        var me = WindowsIdentity.GetCurrent().User!;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.IdentityReference.Equals(me) && r.FileSystemRights.HasFlag(FileSystemRights.FullControl) && r.AccessControlType == AccessControlType.Allow);
        Assert.Contains(rules, r => r.IdentityReference.Equals(System_) && r.FileSystemRights.HasFlag(FileSystemRights.FullControl) && r.AccessControlType == AccessControlType.Allow);
        if (!inheritedAllowed) Assert.All(rules, r => Assert.False(r.IsInherited));
    }

    [Fact]
    public void FolderIsProtectedAndFilesInherit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "murmur-acl-" + Guid.NewGuid().ToString("N"), "root");
        try
        {
            PrivateFiles.PrepareDirectory(dir);
            AssertOwnerAndSystemOnly(new DirectoryInfo(dir).GetAccessControl(), inheritedAllowed: false);

            var file = Path.Combine(dir, "x.json");
            PrivateFiles.Write("{}"u8.ToArray(), file);
            AssertOwnerAndSystemOnly(new FileInfo(file).GetAccessControl(), inheritedAllowed: true);

            // a plain file created by another writer inside the folder inherits the same two entries
            var plain = Path.Combine(dir, "plain.txt");
            File.WriteAllText(plain, "synthetic");
            var inherited = new FileInfo(plain).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            Assert.Equal(2, inherited.Count);
            Assert.All(inherited, r => Assert.True(r.IsInherited));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch (Exception) { } }
    }
}

public class AudioConverterTests
{
    static float[] Sine(double hz, int rate, int channels, double seconds, double amplitude)
    {
        int frames = (int)(rate * seconds);
        var data = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++) data[i * channels + c] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        return data;
    }

    static float Peak(IEnumerable<float> samples) => samples.Select(Math.Abs).DefaultIfEmpty(0).Max();

    [Fact]
    public void StereoFloat48kBecomesMono16kWithSameLengthAndPeak()
    {
        var converter = new AudioConverter(48000, 2, SampleKind.Float32);
        var output = converter.Push(Sine(1000, 48000, 2, 1.0, 0.5)).Concat(converter.Flush()).ToArray();
        Assert.Equal(16000, output.Length);
        Assert.InRange(Peak(output.Skip(200).Take(15600)), 0.48f, 0.52f);
    }

    [Fact]
    public void ChunkedInputGivesTheSameOutputAsOneBlock()
    {
        var input = Sine(440, 48000, 2, 0.5, 0.4);
        var whole = new AudioConverter(48000, 2, SampleKind.Float32);
        var expected = whole.Push(input).Concat(whole.Flush()).ToArray();
        var chunked = new AudioConverter(48000, 2, SampleKind.Float32);
        var got = new List<float>();
        for (int i = 0; i < input.Length; i += 1234 * 2) got.AddRange(chunked.Push(input.AsSpan(i, Math.Min(1234 * 2, input.Length - i))));
        got.AddRange(chunked.Flush());
        Assert.Equal(expected.Length, got.Count);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], got[i], 1e-5f);
    }

    [Fact]
    public void ToneAboveTheOutputBandIsRemoved()
    {
        var converter = new AudioConverter(48000, 1, SampleKind.Float32);
        var output = converter.Push(Sine(15000, 48000, 1, 0.5, 0.8)).Concat(converter.Flush()).ToArray();
        Assert.True(Peak(output.Skip(200)) < 0.05f);
    }

    [Fact]
    public void Pcm16AndMonoAndSameRateWork()
    {
        var samples = Sine(500, 16000, 1, 0.5, 0.5);
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (short)(samples[i] * 32767));
        var converter = new AudioConverter(16000, 1, SampleKind.Pcm16);
        var output = converter.Push(bytes).Concat(converter.Flush()).ToArray();
        Assert.Equal(samples.Length, output.Length);
        Assert.InRange(Peak(output.Skip(100)), 0.48f, 0.52f);
    }

    [Fact]
    public void WavWriterWritesAReadableHeaderAndRefusesToOverwrite()
    {
        var path = Path.Combine(Path.GetTempPath(), "murmur-wav-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            var payload = new byte[4800 * 8];
            using (var w = new AudioCapture.WavWriter(path, 48000, 2, 32, true)) w.Write(payload);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(44 + payload.Length, bytes.Length);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal(payload.Length, BitConverter.ToInt32(bytes, 40));
            Assert.Equal(3, BitConverter.ToInt16(bytes, 20));
            Assert.Equal(2, BitConverter.ToInt16(bytes, 22));
            Assert.Equal(48000, BitConverter.ToInt32(bytes, 24));
            Assert.Throws<IOException>(() => new AudioCapture.WavWriter(path, 48000, 2, 32, true));
        }
        finally { File.Delete(path); }
    }
}

public class AudioCaptureNoDeviceTests
{
    [Fact]
    public void WithoutAMicrophoneListIsEmptyAndStartFailsWithAClearError()
    {
        var devices = AudioCapture.ListDevices();
        if (devices.Count > 0) return; // a real microphone exists on this machine; the VM has none
        var e = Assert.Throws<AudioCaptureException>(() => AudioCapture.Start());
        Assert.Equal(AudioCaptureError.NoDevice, e.Kind);
        Assert.DoesNotContain("0x", e.Message);
    }

    [Fact]
    public void UnknownEndpointIdIsAnError()
    {
        var e = Assert.Throws<AudioCaptureException>(() => AudioCapture.Start("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}"));
        Assert.True(e.Kind is AudioCaptureError.NoDevice or AudioCaptureError.DeviceFailed);
    }
}

public class KeyboardHookTests
{
    [Fact]
    public void InstallsAndDisposesCleanly()
    {
        var hook = new KeyboardHook((in KeyEvent _) => false);
        hook.Dispose();
        hook.Dispose();
    }

    static nint Call(KeyboardHook hook, int vk, bool down, uint flags)
    {
        var mem = System.Runtime.InteropServices.Marshal.AllocHGlobal(32);
        try
        {
            System.Runtime.InteropServices.Marshal.WriteInt32(mem, 0, vk); System.Runtime.InteropServices.Marshal.WriteInt32(mem, 4, 0);
            System.Runtime.InteropServices.Marshal.WriteInt32(mem, 8, (int)flags); System.Runtime.InteropServices.Marshal.WriteInt32(mem, 12, 1);
            return hook.OnKey(0, down ? 0x100u : 0x101u, mem);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(mem); }
    }

    [Fact]
    public void HookLogicFiltersInjectedSwallowsAndTracksChords()
    {
        var seen = new List<KeyEvent>();
        using var hook = new KeyboardHook((in KeyEvent e) => { seen.Add(e); return e.VirtualKey == KeyEvent.VkEscape; });
        const uint extended = 1, injected = 0x10, lowerIl = 2;

        Call(hook, 0x87, true, injected);
        Call(hook, 0x87, true, lowerIl);
        Assert.Empty(seen); // our own (and lower-integrity) injected input never reaches the handler

        Assert.Equal(1, Call(hook, KeyEvent.VkEscape, true, 0)); // swallowed
        Assert.NotEqual(1, Call(hook, 0x41, true, 0));            // passed on (CallNextHookEx result, not 1)

        Call(hook, KeyEvent.VkRControl, true, extended);
        Assert.True(seen[^1].IsRightControl && seen[^1].Extended && seen[^1].IsDown && seen[^1].Modifiers == KeyModifiers.Ctrl);
        Call(hook, KeyEvent.VkRControl, false, extended);
        Assert.Equal(KeyModifiers.None, hook.Modifiers);

        Call(hook, KeyEvent.VkLControl, true, 0); Call(hook, KeyEvent.VkLWin, true, extended); Call(hook, KeyEvent.VkLShift, true, 0);
        Call(hook, 'V', true, 0);
        Assert.True(Chord.PasteLast.Matches(seen[^1]));
        Call(hook, 'V', false, 0); Call(hook, KeyEvent.VkLShift, false, 0); Call(hook, KeyEvent.VkLWin, false, extended); Call(hook, KeyEvent.VkLControl, false, 0);
        Assert.Equal(KeyModifiers.None, hook.Modifiers);

        using var all = new KeyboardHook((in KeyEvent e) => { seen.Add(e); return false; }, ignoreInjected: false);
        seen.Clear();
        Call(all, 0x87, true, injected);
        Assert.True(seen.Single().Injected);
    }

    [InputFact]
    public void InjectedKeysFromSendInputAreIgnoredByDefaultAndReportedWhenAsked()
    {
        var ignored = new List<KeyEvent>();
        var seen = new List<KeyEvent>();
        using (var hook = new KeyboardHook((in KeyEvent e) => { lock (ignored) ignored.Add(e); return false; }))
        using (var all = new KeyboardHook((in KeyEvent e) => { lock (seen) seen.Add(e); return false; }, ignoreInjected: false))
        {
            Injector.Tap(0x87); // F24: no effect on any app
            Thread.Sleep(300);
        }
        lock (ignored) Assert.DoesNotContain(ignored, e => e.VirtualKey == 0x87);
        lock (seen)
        {
            Assert.Contains(seen, e => e.VirtualKey == 0x87 && e.IsDown && e.Injected);
            Assert.Contains(seen, e => e.VirtualKey == 0x87 && !e.IsDown && e.Injected);
        }
    }
}

public class ClipboardPasteTests
{
    static readonly uint FormatA = ClipboardPaste.RegisterFormat("MurmurTest.A"), FormatB = ClipboardPaste.RegisterFormat("MurmurTest.B");
    static readonly object ClipboardLock = new();

    static ClipboardPaste Paster(Func<bool> post, Func<FocusInfo?>? focus = null) =>
        new(TimeSpan.FromMilliseconds(150), focus ?? (() => null)) { PostInput = post };

    static void Seed() => ClipboardPaste.SetFormats((13, Encoding.Unicode.GetBytes("original\0")), (FormatA, [1, 2, 3]), (FormatB, new byte[5000]));

    static void AssertSeeded(Dictionary<uint, byte[]> snap)
    {
        Assert.Equal("original", Encoding.Unicode.GetString(snap[13]).TrimEnd('\0'));
        Assert.Equal(new byte[] { 1, 2, 3 }, snap[FormatA].Take(3).ToArray());
        Assert.True(snap[FormatB].Length >= 5000);
    }

    [Fact]
    public void StagesWithExclusionFormatsThenRestoresEveryFormat()
    {
        lock (ClipboardLock)
        {
            Seed();
            Dictionary<uint, byte[]>? during = null;
            var result = Paster(() => { during = ClipboardPaste.Snapshot(); return true; }).Paste("synthetic dictation");
            Assert.True(result.Pasted);
            Assert.NotNull(during);
            Assert.Equal("synthetic dictation", Encoding.Unicode.GetString(during![13]).TrimEnd('\0'));
            foreach (var f in new[] { ClipboardPaste.Exclude, ClipboardPaste.History, ClipboardPaste.Cloud, ClipboardPaste.Owner }) Assert.True(during.ContainsKey(f));
            Assert.Equal(0, BitConverter.ToInt32(during[ClipboardPaste.History]));
            Assert.Equal(0, BitConverter.ToInt32(during[ClipboardPaste.Cloud]));
            Assert.False(during.ContainsKey(FormatA));
            Thread.Sleep(600);
            var after = ClipboardPaste.Snapshot();
            AssertSeeded(after);
            Assert.False(after.ContainsKey(ClipboardPaste.Owner));
        }
    }

    [Fact]
    public void RestoreIsSkippedWhenAnotherAppReplacedTheClipboard()
    {
        lock (ClipboardLock)
        {
            Seed();
            var result = Paster(() => { ClipboardPaste.SetFormats((13, Encoding.Unicode.GetBytes("someone else\0"))); return true; }).Paste("synthetic dictation");
            Assert.True(result.Pasted);
            Thread.Sleep(600);
            Assert.Equal("someone else", ClipboardPaste.ReadText());
        }
    }

    [Fact]
    public void FlushRestoreRestoresImmediatelyAndOnlyOnce()
    {
        lock (ClipboardLock)
        {
            Seed();
            var paster = new ClipboardPaste(TimeSpan.FromSeconds(30), () => null) { PostInput = () => true };
            Assert.True(paster.Paste("synthetic dictation").Pasted);
            Assert.Equal("synthetic dictation", ClipboardPaste.ReadText());
            paster.FlushRestore();
            AssertSeeded(ClipboardPaste.Snapshot());
            ClipboardPaste.SetFormats((13, Encoding.Unicode.GetBytes("later\0")));
            paster.FlushRestore();
            Assert.Equal("later", ClipboardPaste.ReadText());
        }
    }

    [Fact]
    public void RefusalsLeaveTheClipboardAsItWas()
    {
        lock (ClipboardLock)
        {
            Seed();
            var password = new FocusInfo(1, "app", 0, [1], 50004, true, null, null, false);
            Assert.Equal(PasteFailure.PasswordField, Paster(() => true, () => password).Paste("x").Failure);
            Assert.Equal(PasteFailure.EmptyText, Paster(() => true).Paste("").Failure);
            Assert.Equal(PasteFailure.DestinationChanged, Paster(() => true).Paste("x", () => false).Failure);
            Assert.Equal(PasteFailure.CannotPostInput, Paster(() => false).Paste("x").Failure);
            Assert.Equal(PasteFailure.OwnershipChanged, Paster(() => true).Paste("x", () => { ClipboardPaste.SetFormats((13, Encoding.Unicode.GetBytes("other\0"))); return true; }).Failure);
            Assert.Equal("other", ClipboardPaste.ReadText()); // not ours any more, so not overwritten
            Seed();
            Assert.Equal(PasteFailure.DestinationChanged, Paster(() => true).Paste("x", () => false).Failure);
            AssertSeeded(ClipboardPaste.Snapshot());
        }
    }

    [InputFact]
    public void PastesIntoNotepadThroughSendInput()
    {
        lock (ClipboardLock)
        {
            Seed();
            using var notepad = new NotepadWindow();
            var paster = new ClipboardPaste();
            var result = paster.Paste("synthetic paste check");
            Assert.True(result.Pasted, result.Failure.ToString());
            Thread.Sleep(900);
            Assert.Contains("synthetic paste check", FocusProbe.Capture()?.Value ?? "");
            AssertSeeded(ClipboardPaste.Snapshot());
        }
    }
}

public class FocusProbeTests
{
    [Fact]
    public void CaptureDoesNotThrowWithoutADesktop() => _ = FocusProbe.Capture();

    [SessionFact]
    public void NotepadIsFocusedNotPasswordAndNotMurmur()
    {
        using var notepad = new NotepadWindow();
        var info = FocusProbe.Capture();
        Assert.NotNull(info);
        Assert.Equal("notepad", info!.ProcessName);
        Assert.False(info.IsPassword);
        Assert.False(info.IsMurmur);
        Assert.False(ClipboardPaste.TargetIsElevated(info.ProcessId), "Notepad should not count as elevated");
        Assert.NotEmpty(info.RuntimeId);
        Assert.True(info.SameElement(FocusProbe.Capture()));
        Assert.NotNull(info.Value); // empty document reads as ""
    }
}

static class Injector
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] struct U { [System.Runtime.InteropServices.FieldOffset(0)] public MOUSEINPUT mi; [System.Runtime.InteropServices.FieldOffset(0)] public KEYBDINPUT ki; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct INPUT { public int type; public U u; }
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    public static void Keys(params (int Vk, bool Down, bool Extended)[] keys)
    {
        var inputs = keys.Select(k => new INPUT { type = 1, u = new U { ki = new KEYBDINPUT { wVk = (ushort)k.Vk, dwFlags = (k.Down ? 0u : 2u) | (k.Extended ? 1u : 0u) } } }).ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        Assert.True(sent == inputs.Length, $"SendInput sent {sent} of {inputs.Length} (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}, session {System.Diagnostics.Process.GetCurrentProcess().SessionId})");
    }

    public static void Tap(int vk) => Keys((vk, true, false), (vk, false, false));
}
