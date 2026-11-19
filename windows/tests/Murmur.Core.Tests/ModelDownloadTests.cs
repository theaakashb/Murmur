// Ports app/check_model_download.swift: manifest parsing, the pinned manifests, the download/verify/install state machine against a fake transport (never the
// network), failures, removal, the HTTP transport against an in-memory handler, and the content-free messages. The Swift outputs are in ModelDownloadGoldenTests.
using System.Net;
using System.Net.Http.Headers;
using Murmur.Core;
using static Murmur.Core.Tests.ModelTestSupport;

namespace Murmur.Core.Tests;

public class ModelDownloadTests
{
    [Fact]
    public async Task StartHashesOnAWorkerAndCancelsMidChecksum()
    {
        using var temp = new TempDir();
        using var resume = new ManualResetEventSlim();
        var checkpoint = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture { Contents = [new("large.bin", new byte[16 * 1024 * 1024], true)] };
        var hasher = new PausingHasher(() =>
        {
            checkpoint.SetResult(Environment.CurrentManagedThreadId);
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        });
        var downloader = Downloader(fixture, temp.Combine("model"), hasher: hasher);
        var done = new TaskCompletionSource<ModelDownloadError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            int caller = await Task.Factory.StartNew(() =>
            {
                downloader.Start(null, error => done.SetResult(error));
                return Environment.CurrentManagedThreadId;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(caller, await checkpoint.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(ModelStatus.Verifying, downloader.Status());
            downloader.Cancel();
        }
        finally { resume.Set(); }
        Assert.Equal(ModelDownloadError.Cancelled, await done.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(Directory.Exists(downloader.Target));
    }

    sealed class PausingHasher(Action pause) : IFileHasher
    {
        public string HexDigest(string file, ModelHashAlgorithm algorithm, Func<bool> cancelled)
        {
            int chunks = 0;
            return new StreamingFileHasher(64 * 1024).HexDigest(file, algorithm, () =>
            {
                if (++chunks == 32) pause();
                return cancelled();
            });
        }
    }

    static ModelDownloadError Fails(Action action) => Assert.Throws<ModelDownloadException>(action).Error;

    static ModelDownloader Downloader(Fixture fixture, string target, FakeTransport? transport = null, IFileHasher? hasher = null, Func<string, long?>? free = null)
    {
        var t = transport ?? new FakeTransport();
        if (transport is null) fixture.Serve(t);
        return new ModelDownloader(fixture.Manifest(), target, t, hasher, freeSpace: free ?? (_ => null));
    }

    // MARK: manifest

    [Fact]
    public void ManifestReadsOnlyTheBatchFilesInOrder()
    {
        var fixture = Fixture.Standard();
        var manifest = fixture.Manifest();
        Assert.Equal(fixture.Contents.Select(f => f.Name), manifest.Files.Select(f => f.Rfilename));
        Assert.Equal(fixture.Total, manifest.TotalBytes);
        Assert.True(manifest.Files[0].Algorithm == ModelHashAlgorithm.BlobSha1 && manifest.Files[2].Algorithm == ModelHashAlgorithm.Sha256, "lfs uses sha256, others blob sha1");
        Assert.Equal($"https://huggingface.co/{Repository}/resolve/{Revision}/enc.mlmodelc/weights/weight.bin", manifest.ResolveUrl(manifest.Files[2])!.AbsoluteUri);
        Assert.EndsWith("/a%20b/c%23d.bin", manifest.ResolveUrl(new ModelManifestFile("a b/c#d.bin", 1, "", ModelHashAlgorithm.Sha256))!.AbsoluteUri);
        Assert.Equal("http://localhost:8080/Example/tiny-model/resolve/" + Revision + "/config.json", manifest.ResolveUrl(manifest.Files[0], "http://localhost:8080/")!.AbsoluteUri);
    }

    [Fact]
    public void ManifestRejectsBadInput()
    {
        var fixture = Fixture.Standard();
        Assert.Equal(ModelDownloadError.InvalidManifest, Fails(() => new ModelManifest(fixture.Json(revision: "main"), Repository)));
        Assert.Equal(ModelDownloadError.InvalidManifest, Fails(() => new ModelManifest(fixture.Json(), "Other/model")));
        Assert.Equal(ModelDownloadError.InvalidManifest, Fails(() => new ModelManifest(fixture.Json(batch: ["config.json", "missing.bin"]), Repository)));
        Assert.Equal(ModelDownloadError.InvalidManifest, Fails(() => new ModelManifest(fixture.Json(batch: ["config.json", "config.json"]), Repository)));
        Assert.Equal(ModelDownloadError.InvalidManifest, Fails(() => fixture.Manifest(new() { ["config.json"] = "xyz" })));
        Assert.Equal(ModelDownloadError.InvalidManifest, Fails(() => new ModelManifest("nope", Repository)));
        Assert.Equal(ModelDownloadError.InvalidManifest, Fails(() => new ModelManifest(fixture.Json(batch: []), Repository)));
        Assert.Equal(ModelDownloadError.InvalidManifest, Fails(() => ModelManifest.Load(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-pins.json"), Repository)));
    }

    [Theory]
    [InlineData("../evil")] [InlineData("/etc/passwd")] [InlineData("a/../../b")] [InlineData("a//b")] [InlineData("./a")] [InlineData("a/./b")] [InlineData("a\\..\\b")]
    [InlineData("~/x")] [InlineData("a\0b")] [InlineData("a\nb")] [InlineData("")] [InlineData("x/")] [InlineData("w.part")]
    // Windows only: names that mean something else to NTFS or Win32.
    [InlineData("a:b")] [InlineData("a:stream")] [InlineData("C:evil")] [InlineData("a<b")] [InlineData("a|b")] [InlineData("a?b")] [InlineData("a*b")] [InlineData("a\"b")]
    [InlineData("trailing.")] [InlineData("trailing ")] [InlineData("dir./file")] [InlineData("NUL")] [InlineData("nul.txt")] [InlineData("a/CON")] [InlineData("com1")] [InlineData("LPT9.bin")] [InlineData("AUX.x.y")]
    public void UnsafeNamesAreRejectedBeforeAnythingIsDownloaded(string bad)
    {
        var evil = Fixture.Standard();
        evil.Contents.Add(new(bad, [(byte)'x'], false));
        Assert.Equal(ModelDownloadError.UnsafePath, Fails(() => evil.Manifest()));
        Assert.False(ModelManifest.IsSafeRelativePath(bad));
    }

    [Theory]
    [InlineData("encoder.int8.onnx")] [InlineData("parakeet_unified_decoder.mlmodelc/weights/weight.bin")] [InlineData("a b/c d.bin")] [InlineData("é/ü.bin")] [InlineData("com10")] [InlineData("console.txt")] [InlineData("a.b.c")]
    public void RealisticNamesAreSafe(string good) => Assert.True(ModelManifest.IsSafeRelativePath(good));

    [Fact]
    public void ANameInsideAFileIsRejectedAndRepositoriesAreChecked()
    {
        var nested = Fixture.Standard();
        nested.Contents.Add(new("config.json/inner", [(byte)'x'], false));
        Assert.Equal(ModelDownloadError.UnsafePath, Fails(() => nested.Manifest()));
        Assert.True(!ModelManifest.IsSafeRepository("../x") && !ModelManifest.IsSafeRepository("a/b/c") && ModelManifest.IsSafeRepository("FluidInference/parakeet-unified-en-0.6b-coreml"));
    }

    [Fact]
    public void LicenseIsPinnedOnlyWhenTheManifestRecordsOne()
    {
        var fixture = Fixture.Standard();
        var manifest = fixture.Manifest();
        Assert.True(!manifest.License.IsPinned && manifest.License.Declarations.Count == 0 && manifest.License.Note.Contains("not recorded"), "unknown repo license is honestly unknown");
        var pinned = new ModelManifest(fixture.Json(license: new() { ["name"] = "Example License", ["url"] = "https://example.com/license" }), Repository);
        Assert.True(pinned.License.IsPinned && pinned.License.Declarations[0].Name == "Example License");
        Assert.False(new ModelManifest(fixture.Json(license: new() { ["name"] = "X", ["url"] = "http://example.com" }), Repository).License.IsPinned);
        var declared = new ModelLicenseInfo(false, [new("Card", "L", new Uri("https://x.example/l"))], "note");
        Assert.Equal(declared, new ModelManifest(fixture.Json(), Repository, unpinnedLicense: declared).License);
    }

    // MARK: pinned manifests

    [Fact]
    public void MacPinsStillParse()
    {
        var unified = ModelManifest.Load(PinsPath, "FluidInference/parakeet-unified-en-0.6b-coreml");
        Assert.True(unified.Revision == "d32e972dd4315f1dc3f6be28fb2aab0ab3e80358" && unified.Files.Count == 19 && unified.TotalBytes == 1_801_783_964);
        var live = ModelManifest.Load(PinsPath, "FluidInference/parakeet-unified-en-0.6b-coreml", "live_files");
        Assert.True(live.Files.Count == 13 && live.TotalBytes == 609_438_170);
        var ctc = ModelManifest.Load(PinsPath, "FluidInference/parakeet-ctc-110m-coreml");
        Assert.True(ctc.Files.Count == 12 && ctc.TotalBytes == 102_802_455 && ctc.License.IsPinned && ctc.License.Declarations[0].Name == "CC-BY-4.0");
    }

    [Fact]
    public void WindowsParakeetPinsAreImmutableRevisionsWithHashesForEveryFile()
    {
        var batch = ModelManifest.Load(PinsPath, ModelFacts.Batch.Repository);
        Assert.Equal("8c3a10fb13408c7a7054f6898958bf1c64a8d6c7", batch.Revision);
        Assert.Equal(["decoder.int8.onnx", "encoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"], batch.Files.Select(f => f.Rfilename));
        Assert.Equal(663_043_117, batch.TotalBytes);
        Assert.Equal("6716910b7a0833997fec7a410494c995d70124001a0e9b66d6370d6aced577e0", batch.Files.Single(f => f.Rfilename == "encoder.int8.onnx").Checksum);
        Assert.Equal(654_040_552, batch.Files.Single(f => f.Rfilename == "encoder.int8.onnx").Size);
        Assert.Equal(ModelHashAlgorithm.BlobSha1, batch.Files.Single(f => f.Rfilename == "tokens.txt").Algorithm);
        var live = ModelManifest.Load(PinsPath, ModelFacts.LiveWords.Repository);
        Assert.Equal("7551fd26fc810cc1e4e043e608db4d13b59be31e", live.Revision);
        Assert.Equal(batch.Files.Select(f => f.Rfilename), live.Files.Select(f => f.Rfilename));
        Assert.Equal(663_048_978, live.TotalBytes);
        Assert.Equal("e566c3f014598a41724f2df028779a2d4cf7943cbefa324964f6a72e8ee255fb", live.Files.Single(f => f.Rfilename == "encoder.int8.onnx").Checksum);
        // The upstream license is recorded in the pins, so it shows as pinned.
        foreach (var manifest in new[] { batch, live })
        {
            Assert.True(manifest.License.IsPinned && manifest.License.Declarations.Single().Name == "NVIDIA Open Model License");
            Assert.StartsWith("https://www.nvidia.com/", manifest.License.Declarations.Single().Url.AbsoluteUri);
            Assert.All(manifest.Files, f => Assert.StartsWith($"https://huggingface.co/{manifest.Repository}/resolve/{manifest.Revision}/", manifest.ResolveUrl(f)!.AbsoluteUri));
        }
    }

    [Fact]
    public void TheCleanupModelIsPinnedToTheUpload()
    {
        var manifest = ModelFacts.LocalCleanup.Manifest(BundledModels.Pins(ModelPinsSource.WindowsModels)!);
        Assert.Equal("clouddevaakash/vedu-scribe-0.8b-gguf", manifest.Repository);
        Assert.Equal("edf275972c822fb7787226a89afa4282feb74ec4", manifest.Revision);
        Assert.Equal("vedu-scribe-0.8b-Q8_0.gguf", manifest.Files.Single().Rfilename);
        Assert.Equal(811_843_296, manifest.TotalBytes);
        Assert.Equal("6859e09e41502d2c1665b9462b2f9f37c7e485ebddd14185813f22a89554c708", manifest.Files.Single().Checksum);
        Assert.True(manifest.License.IsPinned && manifest.License.Declarations.Single().Name == "Apache-2.0");
        // The Mac runs the MLX checkpoint; the GGUF stays out of the shared pins it reads.
        Assert.DoesNotContain("vedu-scribe-0.8b-gguf", File.ReadAllText(PinsPath));
    }

    [Fact]
    public void ModelFoldersAreUnderTheDataRootModelsFolder()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "murmur-root");
        var manifest = ModelManifest.Load(PinsPath, ModelFacts.Batch.Repository);
        Assert.Equal(System.IO.Path.Combine(root, "Models", "sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-non-streaming-8c3a10f"), ModelFacts.Batch.Directory(root, manifest));
        var live = ModelManifest.Load(PinsPath, ModelFacts.LiveWords.Repository);
        Assert.Equal(System.IO.Path.Combine(root, "Models", "sherpa-onnx-nemo-parakeet-unified-en-0.6b-int8-streaming-560ms-7551fd2"), ModelFacts.LiveWords.Directory(root, live));
        var cleanup = ModelFacts.LocalCleanup.Manifest(BundledModels.Pins(ModelPinsSource.WindowsModels)!);
        Assert.Equal(System.IO.Path.Combine(root, "Models", "vedu-scribe-0.8b-edf2759"), ModelFacts.LocalCleanup.Directory(root, cleanup));
    }

    // MARK: install

    [Fact]
    public void InstallDownloadsVerifiesAndMovesIntoPlace()
    {
        var fixture = Fixture.Standard();
        var transport = new FakeTransport(); fixture.Serve(transport);
        var hasher = new CountingHasher();
        using var temp = new TempDir();
        var target = temp.Combine("models", "parakeet");
        var downloader = Downloader(fixture, target, transport, hasher, _ => 1L << 40);
        var seen = new List<ModelStatus> { downloader.Status() };
        hasher.Observe = () => seen.Add(downloader.Status());
        var progress = new List<ModelDownloadProgress>();
        Assert.Equal(ModelStatus.NotDownloaded, downloader.Status());
        downloader.Install(p => { progress.Add(p); seen.Add(downloader.Status()); });
        Assert.Equal(ModelStatus.Ready, downloader.Status());
        foreach (var file in fixture.Contents) Assert.Equal(file.Bytes, File.ReadAllBytes(System.IO.Path.Combine(target, file.Name.Replace('/', System.IO.Path.DirectorySeparatorChar))));
        Assert.False(Directory.Exists(downloader.StagingDirectory), "staging moved into place");
        Assert.Equal(fixture.Contents.Select(f => ServedPath(f.Name)), transport.Requests.Select(r => r.Url.AbsolutePath));
        Assert.All(transport.Requests, r => Assert.Equal(0, r.RangeStart));
        Assert.True(progress.Zip(progress.Skip(1)).All(p => p.First.BytesDone <= p.Second.BytesDone), "progress monotonic");
        Assert.Equal(new ModelDownloadProgress(fixture.Total, fixture.Total), progress[^1]);
        Assert.All(progress, p => Assert.Equal(fixture.Total, p.TotalBytes));
        Assert.True(seen[0] == ModelStatus.NotDownloaded && seen.Contains(ModelStatus.Verifying) && seen.Any(s => s.Kind == ModelStatusKind.Downloading), "status passes notDownloaded -> downloading -> verifying");
        Assert.Equal(3, hasher.Calls.Count);
        Assert.Equal(ModelStatus.Ready, downloader.VerifyInstalled());
        Assert.Equal(["parakeet"], Directory.GetFileSystemEntries(System.IO.Path.GetDirectoryName(target)!).Select(System.IO.Path.GetFileName));
        Assert.Equal(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, $".parakeet.download-{Revision[..12]}"), downloader.StagingDirectory);
    }

    [Fact]
    public async Task ASecondInstallOrARemoveWhileRunningIsBusy()
    {
        var fixture = Fixture.Standard();
        var transport = new FakeTransport(); fixture.Serve(transport);
        using var temp = new TempDir();
        var busy = Downloader(fixture, temp.Combine("busy", "model"), transport);
        ModelDownloadError? nested = null, remove = null;
        transport.OnChunk = (_, _) =>
        {
            if (nested is null) { try { busy.Install(); } catch (ModelDownloadException e) { nested = e.Error; } }
            if (remove is null) { try { busy.Remove(); } catch (ModelDownloadException e) { remove = e.Error; } }
        };
        busy.Install();
        Assert.Equal(ModelDownloadError.Busy, nested);
        Assert.Equal(ModelDownloadError.Busy, remove);
        Assert.Equal(ModelStatus.Ready, busy.Status());

        // The worker-thread wrapper.
        var asyncTransport = new FakeTransport(); fixture.Serve(asyncTransport);
        var asyncDownloader = Downloader(fixture, temp.Combine("async", "model"), asyncTransport);
        var done = new TaskCompletionSource<ModelDownloadError?>();
        asyncDownloader.Start(null, e => done.SetResult(e));
        Assert.Null(await done.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(ModelStatus.Ready, asyncDownloader.Status());
        var failing = new FakeTransport(); fixture.Serve(failing); failing.StatusOverride[ServedPath("config.json")] = 500;
        var failed = new TaskCompletionSource<ModelDownloadError?>();
        Downloader(fixture, temp.Combine("fail", "model"), failing).Start(null, e => failed.SetResult(e));
        Assert.Equal(ModelDownloadError.HttpStatus(500), await failed.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ATamperedUpdateIsRejectedAndTheOldModelStays()
    {
        var good = Fixture.Standard();
        using var temp = new TempDir();
        var target = temp.Combine("model");
        Downloader(good, target).Install();
        var weight = System.IO.Path.Combine(target, "enc.mlmodelc", "weights", "weight.bin");
        var original = File.ReadAllBytes(weight);

        var updated = Fixture.Standard(seed: 99);
        var tampered = new FakeTransport(); updated.Serve(tampered);
        var corrupted = (byte[])updated.Contents[2].Bytes.Clone(); corrupted[100] ^= 0xff;
        tampered.Files[ServedPath("enc.mlmodelc/weights/weight.bin")] = corrupted;
        var downloader = Downloader(updated, target, tampered);
        Assert.Equal(ModelDownloadError.ChecksumMismatch, Fails(() => downloader.Install()));
        Assert.Equal(original, File.ReadAllBytes(weight));
        Assert.Equal(good.Contents[1].Bytes, File.ReadAllBytes(System.IO.Path.Combine(target, "enc.mlmodelc", "model.mil")));
        Assert.Equal(ModelStatus.Failed(ModelDownloadError.ChecksumMismatch), downloader.Status());
        var staged = System.IO.Path.Combine(downloader.StagingDirectory, "enc.mlmodelc", "weights", "weight.bin");
        Assert.False(File.Exists(staged) || File.Exists(staged + ".part"), "bad bytes discarded");

        var fresh = temp.Combine("fresh");
        Assert.Equal(ModelDownloadError.ChecksumMismatch, Fails(() => Downloader(updated, fresh, tampered).Install()));
        Assert.False(Directory.Exists(fresh), "no target created on mismatch");

        var oversized = new FakeTransport(); good.Serve(oversized);
        oversized.Files[ServedPath("config.json")] = [.. good.Contents[0].Bytes, .. "extra"u8];
        Assert.Equal(ModelDownloadError.ChecksumMismatch, Fails(() => Downloader(good, temp.Combine("big"), oversized).Install()));

        // A successful retry replaces the old model, and the old copy and staging are cleaned up.
        updated.Serve(tampered);
        downloader.Install();
        Assert.Equal(updated.Contents[2].Bytes, File.ReadAllBytes(weight));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(temp.Path).Select(System.IO.Path.GetFileName), n => n!.StartsWith(".model"));
    }

    [Fact]
    public void CancelKeepsVerifiedFilesAndRetryResumesTheRest()
    {
        var fixture = Fixture.Standard();
        using var temp = new TempDir();
        var target = temp.Combine("model");
        var transport = new FakeTransport(); fixture.Serve(transport);
        var hasher = new CountingHasher();
        var downloader = Downloader(fixture, target, transport, hasher);
        var weight = ServedPath("enc.mlmodelc/weights/weight.bin");
        transport.OnChunk = (p, delivered) => { if (p == weight && delivered >= 20_000) downloader.Cancel(); };
        Assert.Equal(ModelDownloadError.Cancelled, Fails(() => downloader.Install()));
        Assert.False(Directory.Exists(target), "cancel leaves no model at target");
        Assert.Equal(ModelStatus.Failed(ModelDownloadError.Cancelled), downloader.Status());
        var partial = System.IO.Path.Combine(downloader.StagingDirectory, "enc.mlmodelc", "weights", "weight.bin.part");
        var partialSize = new FileInfo(partial).Length;
        Assert.True(partialSize >= 20_000 && partialSize < 70_000, "partial kept in staging for resume");
        Assert.True(File.Exists(System.IO.Path.Combine(downloader.StagingDirectory, "config.json")), "verified file kept in staging");
        downloader.Cancel();   // cancel when idle is harmless

        transport.OnChunk = null;
        var before = transport.Requests.Count;
        hasher.Calls.Clear();
        downloader.Install();
        var retried = transport.Requests.Skip(before).ToList();
        Assert.Equal([weight], retried.Select(r => r.Url.AbsolutePath));
        Assert.Equal(partialSize, retried[0].RangeStart);
        Assert.True(hasher.Calls.Count(c => c == "config.json") == 1 && hasher.Calls.Count == 3, "verified files re-hashed, not downloaded");
        Assert.Equal(fixture.Contents[2].Bytes, File.ReadAllBytes(System.IO.Path.Combine(target, "enc.mlmodelc", "weights", "weight.bin")));
        Assert.Equal(ModelStatus.Ready, downloader.Status());
    }

    [Fact]
    public void AServerThatIgnoresRangeRestartsTheFileAndProgressCorrects()
    {
        var fixture = Fixture.Standard();
        using var temp = new TempDir();
        var noRange = new FakeTransport { IgnoresRange = true }; fixture.Serve(noRange);
        var weight = ServedPath("enc.mlmodelc/weights/weight.bin");
        var restart = Downloader(fixture, temp.Combine("restart"), noRange);
        noRange.FailAfterBytes[weight] = 30_000;
        Assert.Equal(ModelDownloadError.Network, Fails(() => restart.Install()));
        Assert.Equal(ModelStatus.Failed(ModelDownloadError.Network), restart.Status());
        noRange.FailAfterBytes = [];
        ModelDownloadProgress? last = null;
        restart.Install(p => last = p);
        Assert.True(noRange.Requests[^1].RangeStart > 0, "resume was attempted");
        Assert.Equal(fixture.Contents[2].Bytes, File.ReadAllBytes(System.IO.Path.Combine(temp.Combine("restart"), "enc.mlmodelc", "weights", "weight.bin")));
        Assert.Equal(fixture.Total, last!.Value.BytesDone);
    }

    [Fact]
    public void StrayAndAlteredStagedFilesAreNotInstalled()
    {
        var fixture = Fixture.Standard();
        using var temp = new TempDir();
        var stray = Downloader(fixture, temp.Combine("stray"));
        Directory.CreateDirectory(System.IO.Path.Combine(stray.StagingDirectory, "junk"));
        File.WriteAllText(System.IO.Path.Combine(stray.StagingDirectory, "junk", "evil.bin"), "x");
        File.WriteAllText(System.IO.Path.Combine(stray.StagingDirectory, "extra.json"), "x");
        stray.Install();
        var installed = Directory.EnumerateFileSystemEntries(temp.Combine("stray"), "*", SearchOption.AllDirectories).Select(p => System.IO.Path.GetRelativePath(temp.Combine("stray"), p).Replace('\\', '/')).Order().ToArray();
        Assert.Equal(["config.json", "enc.mlmodelc", "enc.mlmodelc/model.mil", "enc.mlmodelc/weights", "enc.mlmodelc/weights/weight.bin"], installed);

        var transport = new FakeTransport(); fixture.Serve(transport);
        var altered = Downloader(fixture, temp.Combine("altered"), transport);
        Directory.CreateDirectory(altered.StagingDirectory);
        File.WriteAllText(System.IO.Path.Combine(altered.StagingDirectory, "config.json"), "{\"name\":\"evil\"}\n");
        altered.Install();
        Assert.Contains(transport.Requests, r => r.Url.AbsolutePath == ServedPath("config.json"));
        Assert.Equal(fixture.Contents[0].Bytes, File.ReadAllBytes(System.IO.Path.Combine(temp.Combine("altered"), "config.json")));
    }

    [Fact]
    public void FailuresAreClassifiedAndClearedByTheNextSuccess()
    {
        var fixture = Fixture.Standard();
        using var temp = new TempDir();
        var weight = ServedPath("enc.mlmodelc/weights/weight.bin");
        var transport = new FakeTransport(); fixture.Serve(transport);
        transport.StatusOverride[weight] = 404;
        var target = temp.Combine("model");
        var downloader = Downloader(fixture, target, transport);
        Assert.Equal(ModelDownloadError.HttpStatus(404), Fails(() => downloader.Install()));
        Assert.Equal(ModelStatus.Failed(ModelDownloadError.HttpStatus(404)), downloader.Status());
        Assert.False(Directory.Exists(target));
        transport.StatusOverride[weight] = 503;
        Assert.Equal(ModelDownloadError.HttpStatus(503), Fails(() => downloader.Install()));

        var full = Downloader(fixture, temp.Combine("full"), new FakeTransport(), null, _ => fixture.Total - 1);
        Assert.Equal(ModelDownloadError.DiskFull, Fails(() => full.Install()));
        Assert.Equal(ModelStatus.Failed(ModelDownloadError.DiskFull), full.Status());

        // An unwritable parent (a file where the folder should be) is a file-system error, not a crash.
        File.WriteAllText(temp.Combine("blocker"), "not a directory");
        Assert.Equal(ModelDownloadError.FileSystem, Fails(() => Downloader(fixture, System.IO.Path.Combine(temp.Combine("blocker"), "model")).Install()));

        transport.StatusOverride = [];
        downloader.Install();
        Assert.Equal(ModelStatus.Ready, downloader.Status());
    }

    [Fact]
    public void WriteErrorsAreClassified()
    {
        Assert.Equal(ModelDownloadError.DiskFull, ModelDownloadError.FromWrite(new IOException("full", 28)));                                    // ENOSPC
        Assert.Equal(ModelDownloadError.DiskFull, ModelDownloadError.FromWrite(new IOException("quota", 122)));                                  // EDQUOT (Linux)
        Assert.Equal(ModelDownloadError.DiskFull, ModelDownloadError.FromWrite(new IOException("full", unchecked((int)0x80070070))));            // ERROR_DISK_FULL
        Assert.Equal(ModelDownloadError.DiskFull, ModelDownloadError.FromWrite(new IOException("full", unchecked((int)0x80070027))));            // ERROR_HANDLE_DISK_FULL
        Assert.Equal(ModelDownloadError.DiskFull, ModelDownloadError.FromWrite(new InvalidOperationException("outer", new IOException("full", 28))));
        Assert.Equal(ModelDownloadError.FileSystem, ModelDownloadError.FromWrite(new IOException("denied", 13)));                                // EACCES
        Assert.Equal(ModelDownloadError.FileSystem, ModelDownloadError.FromWrite(new IOException("x", unchecked((int)0x80070005))));             // ERROR_ACCESS_DENIED
        Assert.Equal(ModelDownloadError.FileSystem, ModelDownloadError.FromWrite(new IOException("not empty", 39)));                             // ENOTEMPTY on Linux is not a Win32 disk-full code
        Assert.Equal(ModelDownloadError.Cancelled, ModelDownloadError.FromWrite(new ModelDownloadException(ModelDownloadError.Cancelled)));
    }

    [Fact]
    public void CorruptionIsFoundCheaplyBySizeAndByHashOnRequest()
    {
        var fixture = Fixture.Standard();
        using var temp = new TempDir();
        var target = temp.Combine("model");
        var downloader = Downloader(fixture, target);
        downloader.Remove();   // removing nothing is fine
        Assert.Equal(ModelStatus.NotDownloaded, downloader.Status());
        downloader.Install();
        var weight = System.IO.Path.Combine(target, "enc.mlmodelc", "weights", "weight.bin");
        var bytes = (byte[])fixture.Contents[2].Bytes.Clone(); bytes[5] ^= 1;
        File.WriteAllBytes(weight, bytes);
        Assert.Equal(ModelStatus.Ready, downloader.Status());   // same-size corruption is invisible to the cheap status
        Assert.Equal(ModelStatus.Corrupt(ModelDownloader.CorruptReason), downloader.VerifyInstalled());
        Assert.Equal(ModelStatus.Corrupt(ModelDownloader.CorruptReason), downloader.VerifyInstalled(() => true));
        File.Delete(System.IO.Path.Combine(target, "config.json"));
        Assert.Equal(ModelStatus.Corrupt(ModelDownloader.CorruptReason), downloader.Status());
    }

    [Fact]
    public void RemoveDeletesTheModelAndAnyPartialDownload()
    {
        var fixture = Fixture.Standard();
        using var temp = new TempDir();
        var target = temp.Combine("model");
        var transport = new FakeTransport(); fixture.Serve(transport);
        Downloader(fixture, target, transport).Install();
        var interrupted = Downloader(fixture, target, transport);
        transport.OnChunk = (_, delivered) => { if (delivered >= 8000) interrupted.Cancel(); };
        Assert.Equal(ModelDownloadError.Cancelled, Fails(() => interrupted.Install()));
        Assert.True(Directory.Exists(interrupted.StagingDirectory), "staging present before remove");
        interrupted.Remove();
        Assert.False(Directory.Exists(target) || Directory.Exists(interrupted.StagingDirectory));
        Assert.Equal(ModelStatus.NotDownloaded, interrupted.Status());
        // A plain file at the target path is reported, not trusted.
        File.WriteAllText(target, "x");
        Assert.Equal(ModelStatus.Corrupt(ModelDownloader.NotDirectoryReason), interrupted.Status());
        interrupted.Remove();
        Assert.Equal(ModelStatus.NotDownloaded, interrupted.Status());
    }

    [Fact]
    public void MessagesNameNoFileOrUrl()
    {
        ModelDownloadError[] all = [ModelDownloadError.Network, ModelDownloadError.DiskFull, ModelDownloadError.ChecksumMismatch, ModelDownloadError.HttpStatus(500), ModelDownloadError.Cancelled,
                                    ModelDownloadError.InvalidManifest, ModelDownloadError.UnsafePath, ModelDownloadError.FileSystem, ModelDownloadError.Busy];
        foreach (var error in all)
        {
            var message = error.UserMessage;
            Assert.True(message.Length > 0 && !message.Contains('/') && !message.Contains('\\') && !message.Contains("huggingface", StringComparison.OrdinalIgnoreCase) && !message.Contains(".bin") && !message.Contains(".onnx"), $"content-free message for {error}");
        }
        Assert.Equal(all.Length, all.Select(e => e.UserMessage).Distinct().Count());
        Assert.Contains("404", ModelDownloadError.HttpStatus(404).UserMessage);
        Assert.NotEqual(ModelDownloadError.HttpStatus(404), ModelDownloadError.HttpStatus(500));
    }

    // MARK: HTTP transport (an in-memory handler: no sockets)

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    static (int Status, byte[] Body) Fetch(HttpModelTransport transport, long rangeStart, Func<bool>? cancelled = null)
    {
        var status = 0; var body = new List<byte>();
        transport.Fetch(new ModelFetchRequest(new Uri("https://models.example/x"), rangeStart), cancelled ?? (() => false), s => status = s, d => body.AddRange(d.ToArray()));
        return (status, [.. body]);
    }

    [Fact]
    public void HttpTransportStreamsBodiesAndSendsRange()
    {
        var handler = new Handler(r =>
        {
            var ok = new HttpResponseMessage(r.Headers.Range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Pattern(200_000)) };
            ok.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return ok;
        });
        var transport = new HttpModelTransport(handler);
        var (status, body) = Fetch(transport, 0);
        Assert.True(status == 200 && body.Length == 200_000 && body.SequenceEqual(Pattern(200_000)));
        Assert.Null(handler.Seen[0].Headers.Range);
        Assert.Equal(206, Fetch(transport, 1234).Status);
        Assert.Equal(1234, handler.Seen[1].Headers.Range!.Ranges.Single().From);
        Assert.Null(handler.Seen[1].Headers.Range!.Ranges.Single().To);
        Assert.Equal(HttpMethod.Get, handler.Seen[1].Method);
        Assert.Equal("https://models.example/x", handler.Seen[1].RequestUri!.AbsoluteUri);
        var notFound = new HttpModelTransport(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) }));
        Assert.Equal(404, Fetch(notFound, 0).Status);
    }

    [Fact]
    public void HttpTransportMapsFailuresAndCancellation()
    {
        var down = new HttpModelTransport(new Handler(_ => throw new HttpRequestException("connection refused")));
        Assert.Equal(ModelDownloadError.Network, Fails(() => Fetch(down, 0)));
        var cancelling = new HttpModelTransport(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Pattern(100)) }));
        Assert.Equal(ModelDownloadError.Cancelled, Fails(() => Fetch(cancelling, 0, () => true)));
        // A callback's own exception comes back unchanged.
        var upset = new HttpModelTransport(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Pattern(100)) }));
        Assert.Equal(ModelDownloadError.DiskFull, Fails(() => upset.Fetch(new ModelFetchRequest(new Uri("https://models.example/x"), 0), () => false, _ => { }, _ => throw new ModelDownloadException(ModelDownloadError.DiskFull))));
    }

    [Fact]
    public void TheDownloaderWorksThroughTheHttpTransport()
    {
        var fixture = Fixture.Standard();
        var handler = new Handler(r =>
        {
            var file = fixture.Contents.FirstOrDefault(f => r.RequestUri!.AbsolutePath == ServedPath(f.Name));
            return file is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Bytes) };
        });
        using var temp = new TempDir();
        var downloader = new ModelDownloader(fixture.Manifest(), temp.Combine("model"), new HttpModelTransport(handler), freeSpace: _ => null);
        downloader.Install();
        Assert.Equal(ModelStatus.Ready, downloader.VerifyInstalled());
        Assert.All(handler.Seen, r => Assert.Equal("huggingface.co", r.RequestUri!.Host));
    }
}
