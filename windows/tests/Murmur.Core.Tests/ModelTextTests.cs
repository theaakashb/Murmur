// The Windows model facts (wording, pins, folders), ModelSlot and ModelFlow. The layouts and gates that come from the Swift code are compared in ModelTextGoldenTests.
using System.Text;
using Murmur.Core;
using static Murmur.Core.Tests.ModelTestSupport;

namespace Murmur.Core.Tests;

public class ModelTextTests
{
    static ModelSlot BatchSlot(string root, IModelDownloadTransport? transport = null) => new(ModelFacts.Batch, File.ReadAllBytes(PinsPath), root, transport);

    [Fact]
    public void BatchFactsSayThisPcAndShowThePinnedLicense()
    {
        var slot = BatchSlot("C:\\data");
        Assert.Equal("663 MB download · English · Runs on this PC", slot.Summary);
        var text = ModelFacts.Batch.Confirmation(slot.Downloader!.DownloadSize, slot.License!, slot.Downloader.Target);
        Assert.Equal("Download Parakeet Unified English 0.6B (663 MB)?", text.Title);
        Assert.Contains("downloads 663 MB from Hugging Face and checks every file against its pinned checksum. After that, transcription runs on this PC without the network.", text.Body);
        Assert.Contains("NVIDIA Open Model License (Pinned model manifest): https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-open-model-license/", text.Body);
        Assert.Contains("This license is recorded in Murmur's pinned model manifest.", text.Body);
        Assert.DoesNotContain("Mac", text.Title + text.Body + slot.Summary + ModelFacts.Batch.RemoveBody("x", 1) + ModelFacts.LiveWords.Explanation + ModelFacts.LocalCleanup.RemoveBody("x", 1));
        Assert.Equal("The model is downloaded and verified. Murmur is ready to dictate.", ModelFacts.Batch.CompletionMessage(null));
        Assert.Equal("Murmur cannot dictate until you download it again (663 MB) or choose another model folder.", ModelFacts.Batch.RemoveBody("x", slot.Downloader.DownloadSize));
        Assert.Equal("Parakeet Unified English 0.6B", slot.Facts.Title);
        Assert.Equal("Parakeet Unified streaming", ModelFacts.LiveWords.Title);
    }

    [Fact]
    public void LiveWordsAndCleanupFacts()
    {
        var live = new ModelSlot(ModelFacts.LiveWords, File.ReadAllBytes(PinsPath), "D:\\r");
        Assert.Equal("Parakeet Unified streaming · 663 MB download · Runs on this PC", live.Summary);
        Assert.Equal("It is saved in /x.", ModelFacts.LiveWords.Placement("/x"));
        Assert.Equal("The bubble goes back to the level meter until you download it again. This deletes /x.", ModelFacts.LiveWords.RemoveBody("/x", 1));

        var cleanup = ModelSlot.Create(ModelFacts.LocalCleanup, "D:\\r");
        Assert.NotNull(cleanup.Downloader);
        Assert.Equal("Vedu Scribe 0.8B · 812 MB download · English · Apache-2.0 · Runs on this PC", cleanup.Summary);
        Assert.Equal("Cleanup then runs on this PC without the network.", ModelFacts.LocalCleanup.Placement("x"));
        Assert.Equal("Cleanup falls back to Local rules until you download it again.", ModelFacts.LocalCleanup.RemoveBody("x", 1));
        Assert.Equal(ModelStatus.NotDownloaded, cleanup.Status);
    }

    [Fact]
    public void ASlotWithoutValidPinsHasNoDownloaderAndSaysSo()
    {
        foreach (var pins in new byte[]?[] { null, Encoding.UTF8.GetBytes("not json"), Encoding.UTF8.GetBytes("{\"models\":{}}") })
        {
            var slot = new ModelSlot(ModelFacts.Batch, pins, "r");
            Assert.True(slot.Downloader is null && slot.Summary == "" && slot.License is null);
            Assert.Equal(ModelDownloadError.InvalidManifest.UserMessage, slot.Row.StatusText);
            Assert.Empty(slot.Row.Actions);
        }
    }

    sealed class Dialogs(bool confirm = true) : IModelFlowDialogs
    {
        public List<(string Title, string Body)> Shown { get; } = [];
        public bool ConfirmDownload(string title, string body) { Shown.Add((title, body)); return confirm; }
        public bool ConfirmRemove(string title, string body) { Shown.Add((title, body)); return confirm; }
    }

    /// <summary>A pins file for a tiny model, so the flow downloads a few bytes instead of 663 MB.</summary>
    static ModelSlot TinySlot(string root, FakeTransport transport, out Fixture fixture)
    {
        fixture = Fixture.Standard();
        fixture.Serve(transport);
        var facts = ModelFacts.Batch with { Repository = Repository, FolderName = "tiny" };
        return new ModelSlot(facts, Encoding.UTF8.GetBytes(fixture.Json(license: new() { ["name"] = "Example License", ["url"] = "https://example.com/l" })), root, transport);
    }

    [Fact]
    public async Task DownloadAsksFirstThenFetchesVerifiesAndReports()
    {
        using var temp = new TempDir();
        var transport = new FakeTransport();
        var slot = TinySlot(temp.Path, transport, out var fixture);
        var dialogs = new Dialogs();
        var done = new TaskCompletionSource<ModelDownloadError?>();
        var changes = 0;
        var gateCalls = 0;
        var flow = new ModelFlow(slot, dialogs) { DownloadGate = () => { gateCalls++; return null; }, DidDownload = e => done.SetResult(e), Changed = () => changes++, PushProgress = true };
        Assert.Equal(System.IO.Path.Combine(temp.Path, "Models", $"tiny-{Revision[..7]}"), slot.Downloader!.Target);
        flow.Download();
        Assert.Null(await done.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Single(dialogs.Shown);
        Assert.Equal("Download Parakeet Unified English 0.6B (79 KB)?", dialogs.Shown[0].Title);
        Assert.Contains("Example License", dialogs.Shown[0].Body);
        Assert.True(gateCalls == 2, "the gate is checked again after the dialog");
        Assert.True(slot.Status == ModelStatus.Ready && !slot.InFlight && slot.Message == "The model is downloaded and verified. Murmur is ready to dictate.");
        Assert.True(changes >= 3);
        Assert.Equal(fixture.Contents[0].Bytes, File.ReadAllBytes(System.IO.Path.Combine(slot.Downloader.Target, "config.json")));
        Assert.Equal([ModelRowAction.Remove], slot.Row.Actions);
    }

    [Fact]
    public void DownloadStopsAtTheDialogTheGateAndAMissingManifest()
    {
        using var temp = new TempDir();
        var transport = new FakeTransport();
        var slot = TinySlot(temp.Path, transport, out _);
        new ModelFlow(slot, new Dialogs(confirm: false)).Download();
        Assert.True(slot.Status == ModelStatus.NotDownloaded && !slot.InFlight && transport.Requests.Count == 0, "Cancel in the dialog fetches nothing");
        new ModelFlow(slot, new Dialogs()) { DownloadGate = () => ModelGate.DownloadRefusal(isBusy: true, preview: false, downloading: false) }.Download();
        Assert.Equal("Finish the current dictation before downloading the model.", slot.Message);
        Assert.Empty(transport.Requests);
        var empty = new ModelSlot(ModelFacts.Batch, null, "r");
        new ModelFlow(empty, new Dialogs()).Download();
        Assert.Equal(ModelDownloadError.InvalidManifest.UserMessage, empty.Message);
        var inFlight = new ModelSlot(ModelFacts.Batch, File.ReadAllBytes(PinsPath), "r") { InFlight = true };
        var dialogs = new Dialogs();
        new ModelFlow(inFlight, dialogs).Download();
        Assert.Empty(dialogs.Shown);
    }

    [Fact]
    public async Task RemoveConfirmsReleasesThenDeletes()
    {
        using var temp = new TempDir();
        var transport = new FakeTransport();
        var slot = TinySlot(temp.Path, transport, out _);
        slot.Downloader!.Install();
        slot.Status = slot.Downloader.Status();
        var order = new List<string>();
        var dialogs = new Dialogs();
        var flow = new ModelFlow(slot, dialogs) { Release = go => { order.Add("release"); go(); order.Add("went"); }, DidRemove = () => order.Add("removed") };
        flow.Remove();
        Assert.Equal(["release", "removed", "went"], order);
        Assert.Equal("The model was removed.", slot.Message);
        Assert.Equal(ModelStatus.NotDownloaded, slot.Status);
        Assert.False(Directory.Exists(slot.Downloader.Target));
        Assert.Equal("Remove the downloaded model?", dialogs.Shown[0].Title);
        // Refused while a download runs; nothing is deleted when the user says no.
        slot.Downloader.Install();
        slot.Status = ModelStatus.Ready;
        new ModelFlow(slot, new Dialogs(confirm: false)).Remove();
        Assert.True(Directory.Exists(slot.Downloader.Target));
        new ModelFlow(slot, new Dialogs()) { RemoveGate = () => ModelGate.RemoveRefusal(isBusy: false, preview: false, downloading: true) }.Remove();
        Assert.Equal("Cancel the download before removing the model.", slot.Message);
        Assert.True(Directory.Exists(slot.Downloader.Target));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProgressAndCompletionGoThroughPost()
    {
        using var temp = new TempDir();
        var transport = new FakeTransport();
        var slot = TinySlot(temp.Path, transport, out _);
        var done = new TaskCompletionSource<ModelDownloadError?>();
        var posted = 0;
        new ModelFlow(slot, new Dialogs()) { Post = a => { Interlocked.Increment(ref posted); a(); }, DidDownload = e => done.SetResult(e), PushProgress = true }.Download();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(posted > 2, "worker-thread callbacks are marshalled");
    }

    [Fact]
    public async Task ACancelledDownloadSaysRetryContinues()
    {
        using var temp = new TempDir();
        var transport = new FakeTransport();
        var slot = TinySlot(temp.Path, transport, out _);
        var done = new TaskCompletionSource<ModelDownloadError?>();
        var flow = new ModelFlow(slot, new Dialogs()) { DidDownload = e => done.SetResult(e) };
        transport.OnChunk = (_, delivered) => { if (delivered >= 8000) flow.Cancel(); };
        flow.Download();
        Assert.Equal(ModelDownloadError.Cancelled, await done.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("The download was cancelled. Retry continues from the files already downloaded.", slot.Message);
        Assert.Equal([ModelRowAction.Retry, ModelRowAction.Remove], slot.Row.Actions);
    }
}
