using Murmur.Core;

namespace Murmur.Core.Tests;

public sealed class CallDetectionTests
{
    [Fact]
    public void OfferTimingMatchesSwiftWithWindowsProcessNames()
    {
        foreach (var scenario in GoldenFiles.Load("call_detection").EnumerateArray())
        {
            var detector = new CallDetector();
            var offers = new List<int>();
            foreach (var tick in scenario.GetProperty("ticks").EnumerateArray())
            {
                bool Flag(string name) => tick.GetProperty(name).GetBoolean();
                int now = tick.GetProperty("now").GetInt32();
                if (Flag("rearm")) detector.Rearm("ZOOM.EXE");
                var result = detector.Update(Flag("active") ? ["ZOOM.EXE"] : [], now,
                    new(Flag("enabled"), Flag("never") ? ["Zoom.exe"] : [], Flag("meeting"), Flag("dictating")));
                if (result is not null) offers.Add(now);
            }
            Assert.Equal(scenario.GetProperty("offers").EnumerateArray().Select(item => item.GetInt32()), offers);
        }
    }

    [Fact]
    public void KnownAppsAndBrowsersOfferButOwnOrUnlistedProcessesDoNot()
    {
        Assert.Equal("Microsoft Teams", CallApp.Find("MS-Teams.exe")?.Name);
        Assert.Equal("Webex", CallApp.Find("CiscoCollabHost")?.Name);
        Assert.Null(CallApp.Find("chromeish"));
        Assert.Equal(CallApp.Catalog.Count, CallApp.Catalog.Select(app => app.ProcessName).Distinct().Count());
        var detector = new CallDetector();
        var conditions = new CallDetector.Conditions(OwnProcesses: new HashSet<string> { "zoom" });
        Assert.Null(detector.Update(["zoom.exe", "music", ""], 0, conditions));
        Assert.Null(detector.Update(["ZOOM.EXE", "music", ""], 5, conditions));
        Assert.Null(detector.Update(["chrome"], 10));
        var browser = Assert.IsType<CallOffer>(detector.Update(["CHROME.EXE"], 15));
        Assert.Equal("Google Chrome is using the microphone.", browser.Text);
        Assert.Equal("If this is a call, take meeting notes?", browser.Question);
        Assert.Equal(new MeetingSource("Google Chrome", "chrome"), browser.Source);
        var zoom = new CallOffer(CallApp.Find("zoom")!);
        Assert.Equal("Take meeting notes?", zoom.Question);
        Assert.Equal("Never for Zoom", zoom.NeverLabel);
    }

    [Fact]
    public void NeverListUsesCanonicalNames()
    {
        var settings = new InMemorySettingsStore();
        Assert.True(CallDetectionSettings.Enabled(settings));
        CallDetectionSettings.SetEnabled(settings, false);
        Assert.False(CallDetectionSettings.Enabled(settings));
        CallDetectionSettings.AddNever(settings, "ZOOM.EXE");
        CallDetectionSettings.AddNever(settings, "Zoom");
        Assert.Equal(new[] { "zoom" }, CallDetectionSettings.Never(settings));
        CallDetectionSettings.RemoveNever(settings, "zoom.exe");
        Assert.Empty(CallDetectionSettings.Never(settings));
    }
}
