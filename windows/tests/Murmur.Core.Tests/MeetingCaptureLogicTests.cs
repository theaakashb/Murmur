using Murmur.Core;

namespace Murmur.Core.Tests;

public sealed class MeetingCaptureLogicTests
{
    [Fact]
    public void SilenceFillKeepsTheClockWhileNothingArrives()
    {
        var fill = new SilenceFill();
        fill.Begin(10);
        Assert.Equal(0, fill.Pieces(10.15));               // inside the tolerance
        Assert.Equal(9, fill.Pieces(11.05));               // 0.85 s behind, in 0.1 s pieces, until under the tolerance
        fill.Audio(11.05, 1600);                           // a real 0.1 s packet
        Assert.Equal(0, fill.Pieces(11.2));
        Assert.Equal(1, fill.Pieces(11.45));               // 0.1 s behind the packet's end plus the tolerance
    }

    [Fact]
    public void SilenceFillTotalStaysWithinToleranceOfWallClock()
    {
        var fill = new SilenceFill();
        fill.Begin(0);
        double samples = 0;
        for (double now = 0; now <= 60; now += 0.01)
        {
            samples += fill.Pieces(now) * SilenceFill.PieceFrames;
            if (now is > 20 and < 21 && Math.Abs(now % 0.1) < 0.005) { fill.Audio(now, 1600); samples += 1600; }
        }
        Assert.InRange(samples / AudioFormat.SampleRate, 60 - SilenceFill.Tolerance - 0.11, 60);
    }

    static readonly ProcessInfo[] Processes =
    [
        new(100, 4, "explorer.exe"), new(200, 100, "ms-teams.exe"), new(210, 200, "msedgewebview2.exe"), new(220, 210, "msedgewebview2.exe"),
        new(300, 100, "Zoom.exe"), new(310, 300, "CptHost.exe"), new(400, 100, "chrome.exe"), new(410, 400, "chrome.exe"), new(500, 100, "Murmur.exe"), new(510, 500, "MurmurSpeech.exe"),
        new(600, 100, "chrome.exe"),
    ];

    [Fact]
    public void RootsAreTheAppProcessesWhoseParentIsNotTheApp()
    {
        Assert.Equal([400, 600], ProcessTargets.Roots(Processes, CallApp.Find("chrome")!));
        Assert.Equal([200], ProcessTargets.Roots(Processes, CallApp.Find("ms-teams")!));
        Assert.Empty(ProcessTargets.Roots(Processes, CallApp.Find("slack")!));
        Assert.Equal([200, 210, 220], ProcessTargets.Tree(Processes, [200]).Order());
    }

    [Fact]
    public void PickPrefersTheRootThatIsPlaying()
    {
        Assert.Equal(400, ProcessTargets.Pick([400, 600], Processes, []));
        Assert.Equal(600, ProcessTargets.Pick([400, 600], Processes, [new(600, false, true)]));
        Assert.Null(ProcessTargets.Pick([], Processes, []));
    }

    [Fact]
    public void WebviewAudioCountsForTeamsAndMurmurNeverCountsAsPlaying()
    {
        var teams = new CallTarget.Only(CallApp.Find("ms-teams")!);
        Assert.True(ProcessTargets.IsPlaying(Processes, teams, [new(220, false, true)]));
        Assert.False(ProcessTargets.IsPlaying(Processes, teams, [new(400, false, true)]));
        var all = new CallTarget.AllExcept(500);
        Assert.False(ProcessTargets.IsPlaying(Processes, all, [new(510, false, true)]));
        Assert.True(ProcessTargets.IsPlaying(Processes, all, [new(400, false, true)]));
    }

    [Fact]
    public void MicrophoneUseIsAttributedToTheCatalogAppAboveAHelper()
    {
        var clients = new AudioClient[] { new(220, true, false), new(510, true, false), new(100, true, false), new(310, true, false) };
        Assert.Equal(["ms-teams", "zoom"], ProcessTargets.InputApps(Processes, clients, 500));
        Assert.True(ProcessTargets.UsesMicrophone(Processes, clients, "MS-Teams.exe", 500));
        Assert.False(ProcessTargets.UsesMicrophone(Processes, [new(510, true, false)], "chrome", 500));
    }

    [Fact]
    public void AParentCycleOrMissingParentDoesNotHang()
    {
        ProcessInfo[] odd = [new(1, 2, "a.exe"), new(2, 1, "b.exe"), new(3, 99, "zoom.exe")];
        Assert.Equal(["zoom"], ProcessTargets.InputApps(odd, [new(3, true, false), new(1, true, false)], 7));
        Assert.Equal([3], ProcessTargets.Roots(odd, CallApp.Find("zoom")!));
    }
}
