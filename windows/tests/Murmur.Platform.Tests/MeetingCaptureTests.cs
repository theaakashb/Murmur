using System.Diagnostics;
using System.Runtime.InteropServices;
using Murmur.Core;
using Murmur.Platform;

namespace Murmur.Platform.Tests;

/// <summary>Needs a real audio output device and microphone (the user's PC): set MURMUR_REAL_AUDIO=1. The agents VM has no audio devices.</summary>
public sealed class RealAudioFactAttribute : FactAttribute
{
    public RealAudioFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MURMUR_REAL_AUDIO") != "1") Skip = "Needs audio devices; set MURMUR_REAL_AUDIO=1 on a PC with a speaker and microphone";
    }
}

public class MeetingCaptureTests(Xunit.ITestOutputHelper output)
{
    [Fact]
    public void ProcessListHasThisProcessWithItsParent()
    {
        var all = AudioSessions.Processes();
        var me = all.Single(p => p.Pid == Environment.ProcessId);
        Assert.Contains(all, p => p.Pid == me.ParentPid);
        Assert.Equal(Process.GetCurrentProcess().ProcessName, me.Name.Replace(".exe", ""), ignoreCase: true);
    }

    [Fact]
    public void SessionEnumerationNeverThrowsWithOrWithoutDevices() => Assert.NotNull(AudioSessions.Clients());

    [Fact]
    public void CallAudioActivationFailsCleanlyOrWorksButNeverCrashes()
    {
        var audio = new WasapiMeetingAudio(() => null);
        Assert.Equal(Environment.OSVersion.Version.Build >= ProcessLoopback.MinimumBuild, audio.CallAudioSupported);
        IMeetingStream? stream = null;
        try { stream = audio.StartCall(MeetingSource.Everything, _ => { }); Thread.Sleep(500); output.WriteLine($"activation worked, buffers={stream.Buffers}"); }
        catch (MeetingCaptureException e) { output.WriteLine("activation failed: " + e.Message); Assert.DoesNotContain("Exception", e.Message); } // content-free, readable message
        finally { stream?.Stop(); }
    }

    [Fact]
    public void MicrophoneStartFailsCleanlyWithoutADevice()
    {
        IMeetingStream? stream = null;
        try { stream = new WasapiMeetingAudio(() => null).StartMicrophone(_ => { }); }
        catch (MeetingCaptureException) { }
        finally { stream?.Stop(); }
    }

    [Fact]
    public void ANamedAppWithNoRunningProcessWaitsInsteadOfFailing()
    {
        // No Slack running: the stream starts idle (silence only) until a root appears, as a tap with no processes does on the Mac.
        if (AudioSessions.Processes().Any(p => CallApp.Normalize(p.Name) == "slack")) return;
        IMeetingStream? stream = null;
        try
        {
            stream = new WasapiMeetingAudio(() => null).StartCall(new("Slack", "slack"), _ => { });
            Thread.Sleep(300);
            Assert.False(stream.HeardSignal);
        }
        catch (MeetingCaptureException) { /* before Windows 20348 */ }
        finally { stream?.Stop(); }
    }

    [RealAudioFact]
    public void LoopbackHearsAnotherProcessesToneAndStaysAlignedThroughSilence()
    {
        // Everything except this process's parent, so this process's own playback is captured.
        int parent = AudioSessions.Processes().Single(p => p.Pid == Environment.ProcessId).ParentPid;
        long samples = 0;
        var tone = Wav(440, 2);
        var clock = Stopwatch.StartNew();
        var stream = ProcessLoopback.Start(new CallTarget.AllExcept(parent), block => Interlocked.Add(ref samples, block.Length));
        try
        {
            PlaySound(tone, 0, 0x6); // SND_MEMORY | SND_SYNC
            Assert.True(stream.HeardSignal, "the tone should be captured");
            Thread.Sleep(3000); // silence: no packets, so the fill must keep the count moving
        }
        finally { stream.Stop(); }
        double captured = Interlocked.Read(ref samples) / (double)AudioFormat.SampleRate;
        Assert.InRange(captured, clock.Elapsed.TotalSeconds - 0.7, clock.Elapsed.TotalSeconds + 0.3);
    }

    [DllImport("winmm.dll")] static extern bool PlaySound(byte[] sound, nint module, uint flags);

    static byte[] Wav(double hz, double seconds)
    {
        int rate = 16000, n = (int)(rate * seconds);
        var data = new byte[44 + n * 2];
        void Put(int at, byte[] b) => b.CopyTo(data, at);
        Put(0, "RIFF"u8.ToArray()); Put(4, BitConverter.GetBytes(36 + n * 2)); Put(8, "WAVEfmt "u8.ToArray()); Put(16, BitConverter.GetBytes(16)); Put(20, BitConverter.GetBytes((short)1));
        Put(22, BitConverter.GetBytes((short)1)); Put(24, BitConverter.GetBytes(rate)); Put(28, BitConverter.GetBytes(rate * 2)); Put(32, BitConverter.GetBytes((short)2)); Put(34, BitConverter.GetBytes((short)16));
        Put(36, "data"u8.ToArray()); Put(40, BitConverter.GetBytes(n * 2));
        for (int i = 0; i < n; i++) Put(44 + i * 2, BitConverter.GetBytes((short)(12000 * Math.Sin(2 * Math.PI * hz * i / rate))));
        return data;
    }
}
