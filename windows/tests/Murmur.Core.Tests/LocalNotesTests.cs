using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>Gemma 3 4B as the Windows default local notes model: the pin, the licence notice, the no-GPU warning and where it shows, the progress and estimate, and the route.</summary>
[Collection("NotesGpu")]
public sealed class LocalNotesTests
{
    const string Warning = "No compatible GPU found. Local meeting notes may take 10 minutes or more for a 30-minute meeting. A Custom Provider, or a PC with a compatible GPU, is much faster.";

    static ModelManifest Manifest() => ModelFacts.LocalNotes.Manifest(BundledModels.Pins(ModelPinsSource.WindowsModels)!);

    [Fact]
    public void ThePinIsTheBenchmarkedFile()
    {
        var manifest = Manifest();
        Assert.Equal("ggml-org/gemma-3-4b-it-GGUF", manifest.Repository);
        Assert.Equal("d0976223747697cb51e056d85c532013931fe52e", manifest.Revision);
        var file = Assert.Single(manifest.Files);
        Assert.Equal("gemma-3-4b-it-Q4_K_M.gguf", file.Rfilename);
        Assert.Equal(2_489_757_856, file.Size);
        Assert.Equal("882e8d2db44dc554fb0ea5077cb7e4bc49e7342a1f0da57901c0802ea21a0863", file.Checksum);
        Assert.Equal(ModelHashAlgorithm.Sha256, file.Algorithm);
        Assert.Equal(2_489_757_856, manifest.TotalBytes);
        Assert.True(manifest.License.IsPinned);
        Assert.Equal("Gemma Terms of Use", Assert.Single(manifest.License.Declarations).Name);
        Assert.Equal("https://huggingface.co/ggml-org/gemma-3-4b-it-GGUF/resolve/d0976223747697cb51e056d85c532013931fe52e/gemma-3-4b-it-Q4_K_M.gguf",
            manifest.ResolveUrl(file)!.AbsoluteUri);
        Assert.EndsWith(Path.Combine("Models", "gemma-3-4b-it-d097622"), ModelFacts.LocalNotes.Directory("D", manifest));
    }

    [Fact]
    public void TheSlotHasADownloaderAndTheRowFacts()
    {
        var slot = new ModelSlot(ModelFacts.LocalNotes, BundledModels.Pins(ModelPinsSource.WindowsModels), Path.Combine(Path.GetTempPath(), "murmur-notes-slot-" + Guid.NewGuid().ToString("N")));
        Assert.NotNull(slot.Downloader);
        Assert.Equal("Gemma 3 4B (meeting notes)", slot.Facts.Title);
        Assert.Equal("Gemma 3 4B · 2.49 GB download · English · Gemma terms · Runs on this PC", slot.Summary);
    }

    [Fact]
    public void TheWarningIsTheDecidedCopyAndOnlyForAKnownAbsence()
    {
        Assert.Equal(Warning, NotesGpu.NoGpuWarning);
        Assert.Equal(Warning, NotesGpu.Warning(false));
        Assert.Null(NotesGpu.Warning(true));
        Assert.Null(NotesGpu.Warning(null));
        // Sentence case, the Mac's punctuation: one sentence per claim, a full stop at the end, no exclamation mark or dash.
        Assert.DoesNotContain("!", Warning);
        Assert.DoesNotContain("—", Warning);
        Assert.EndsWith(".", Warning);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TheDownloadConfirmationWarnsOnlyWithoutAGpu(bool gpu, bool warns)
    {
        var before = NotesGpu.Probe;
        try
        {
            NotesGpu.Probe = _ => Task.FromResult(gpu);
            NotesGpu.Reset();
            Assert.Equal(gpu, await NotesGpu.Available());
            var (title, body) = ModelFacts.LocalNotes.Confirmation(2_489_757_856, Manifest().License, @"D:\Models\gemma-3-4b-it-d097622");
            Assert.Equal("Download Gemma 3 4B (2.49 GB)?", title);
            Assert.Equal(warns, body.Contains(Warning, StringComparison.Ordinal));
            if (warns) Assert.EndsWith("\n\n" + Warning, body);
            Assert.Contains("Gemma Terms of Use: https://ai.google.dev/gemma/terms", body);
            Assert.Contains("https://ai.google.dev/gemma/prohibited_use_policy", body);
            Assert.Contains("checks every file against its pinned checksum", body);
        }
        finally { NotesGpu.Probe = before; NotesGpu.Reset(); }
    }

    [Fact]
    public async Task AFailedOrPendingCheckShowsNoWarning()
    {
        var before = NotesGpu.Probe;
        try
        {
            NotesGpu.Probe = _ => Task.FromException<bool>(new IOException());
            NotesGpu.Reset();
            await Assert.ThrowsAsync<IOException>(() => NotesGpu.Available());
            Assert.Null(NotesGpu.Known);
            Assert.Null(NotesGpu.Warning(NotesGpu.Known));
        }
        finally { NotesGpu.Probe = before; NotesGpu.Reset(); }
    }

    [Fact]
    public void ProgressShowsStepsAndAnEstimateOnCpuOnly()
    {
        Assert.Equal(510, NotesProgress.EstimateSeconds(30, onGpu: false));   // the 5900X: about 8.5 minutes
        Assert.Equal(60, NotesProgress.EstimateSeconds(30, onGpu: true));

        Assert.Equal("Starting… · about 9 minutes left", new NotesProgress(0, 8, false, 0, 510).Text);
        Assert.Equal("3 of 8 steps done · about 5 minutes left", new NotesProgress(3, 8, false, 180).Text);   // 60 s a step, 5 left
        Assert.Equal("7 of 8 steps done · about a minute left", new NotesProgress(7, 8, false, 420).Text);
        Assert.Equal("8 of 8 steps done · less than a minute left", new NotesProgress(8, 8, false, 480).Text);
        Assert.Equal("Writing notes…", new NotesProgress(0, 8, true, 0).Text);
        Assert.Equal("3 of 8 steps done", new NotesProgress(3, 8, true, 20).Text);
        Assert.Equal("Starting…", new NotesProgress(0, 8, false, 0).Text);
        Assert.Equal("less than a minute left", NotesProgress.Remaining(44));
        Assert.Equal("about 10 minutes left", NotesProgress.Remaining(600));
    }

    [Fact]
    public async Task TheWriterReportsFinishedAndPlannedRequests()
    {
        var meeting = MeetingTests.NewMeeting();
        for (int i = 0; i < 6; i++) meeting.Insert(MeetingStream.Them, i * 20, i * 20 + 10, $"We reviewed alpha and bravo for the plan, part {i}.");
        var reports = new List<(int Done, int Planned)>();
        var outcome = await MeetingNotesWriter.WriteAsync(meeting, MeetingNotesRoute.LocalModel, LocalNotesIdentity.NotesRouteName,
            (prompt, _) => Task.FromResult(prompt.System.StartsWith("You merge", StringComparison.Ordinal) ? "TITLE | Plan review\nPOINT | L1 | Alpha plan" : "POINT | L1 | Alpha plan"),
            chunkCharacters: 90, progress: (done, planned) => reports.Add((done, planned)));
        Assert.Equal("Gemma 3 4B (on this PC)", outcome.Notes.Route);
        Assert.NotEmpty(reports);
        Assert.Equal(0, reports[0].Done);
        Assert.True(reports[0].Planned >= 2);
        Assert.Equal(reports.Select(r => r.Done).OrderBy(d => d), reports.Select(r => r.Done));
        Assert.All(reports, r => Assert.True(r.Done <= r.Planned));
        Assert.Equal(reports[^1].Planned, reports[^1].Done);
    }

    [Fact]
    public void TheLocalRouteIsASeparateSetting()
    {
        var settings = new InMemorySettingsStore();
        Assert.Null(MeetingNotesRoutes.Load(settings));
        settings.Set("meetingNotesRoute", MeetingNotesRoute.LocalModel.SettingValue());
        Assert.Equal(MeetingNotesRoute.LocalModel, MeetingNotesRoutes.Load(settings));
        Assert.Equal("On this PC (Gemma 3 4B)", MeetingNotesRoute.LocalModel.Label());
        Assert.Equal("custom-api", MeetingNotesRoute.CustomProvider.SettingValue());
        Assert.Equal("none", MeetingNotesRoute.None.SettingValue());
        Assert.Equal("Notes are written on this PC by Gemma 3 4B. Nothing leaves this PC.", MeetingNotesRoute.LocalModel.Disclosure(null, true, null));
    }
}
