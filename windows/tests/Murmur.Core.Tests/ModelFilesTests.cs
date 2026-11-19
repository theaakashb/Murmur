// Verifying the pinned model files on disk (sizes, checksums, the per-launch cache) and where the bundled pins live.
using Murmur.Core;
using static Murmur.Core.Tests.ModelTestSupport;

namespace Murmur.Core.Tests;

public class ModelFilesTests
{
    static string Install(TempDir temp, Fixture fixture)
    {
        var target = temp.Combine("model");
        foreach (var file in fixture.Contents)
        {
            var path = System.IO.Path.Combine(target, file.Name.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Bytes);
        }
        return target;
    }

    static string Message(Action action) => Assert.Throws<RetentionError>(action).Message;

    [Fact]
    public void VerifyAcceptsTheInstalledFilesAndNamesTheFirstProblem()
    {
        var fixture = Fixture.Standard();
        var manifest = fixture.Manifest();
        using var temp = new TempDir();
        var target = Install(temp, fixture);
        ModelFiles.VerifyAppModels(target, manifest);
        var weight = System.IO.Path.Combine(target, "enc.mlmodelc", "weights", "weight.bin");
        var bytes = File.ReadAllBytes(weight); bytes[9] ^= 1;
        File.WriteAllBytes(weight, bytes);
        Assert.Equal("Model checksum mismatch", Message(() => ModelFiles.VerifyAppModels(target, manifest)));
        File.WriteAllBytes(weight, bytes[..^1]);
        Assert.Equal("Model file size mismatch", Message(() => ModelFiles.VerifyAppModels(target, manifest)));
        File.Delete(weight);
        Assert.Equal("Missing model file", Message(() => ModelFiles.VerifyAppModels(target, manifest)));
    }

    [Fact]
    public void TheCacheRehashesWhenAFileChanges()
    {
        var fixture = Fixture.Standard();
        var manifest = fixture.Manifest();
        using var temp = new TempDir();
        var target = Install(temp, fixture);
        var cache = new ModelVerificationCache();
        cache.Verify(target, manifest);
        cache.Verify(target, manifest);
        var weight = System.IO.Path.Combine(target, "enc.mlmodelc", "weights", "weight.bin");
        var before = ModelVerificationCache.Fingerprint(target, manifest);
        var bytes = File.ReadAllBytes(weight); bytes[3] ^= 1;
        File.WriteAllBytes(weight, bytes);
        File.SetLastWriteTimeUtc(weight, File.GetLastWriteTimeUtc(weight).AddSeconds(5));
        Assert.NotEqual(before, ModelVerificationCache.Fingerprint(target, manifest));
        Assert.Equal("Model checksum mismatch", Message(() => cache.Verify(target, manifest)));
        // A refused folder is not remembered; fixing it verifies again, and Forget drops a folder after a remove or download.
        bytes[3] ^= 1;
        File.WriteAllBytes(weight, bytes);
        cache.Verify(target, manifest);
        cache.Forget(target);
        cache.Verify(target, manifest);
    }

    [Fact]
    public void ACacheMissingFileIsReportedWithoutHashing()
    {
        var fixture = Fixture.Standard();
        using var temp = new TempDir();
        var target = Install(temp, fixture);
        File.Delete(System.IO.Path.Combine(target, "config.json"));
        Assert.Equal("Missing model file", Message(() => new ModelVerificationCache().Verify(target, fixture.Manifest())));
        Assert.Equal("Missing model file", Message(() => ModelVerificationCache.Fingerprint(target, fixture.Manifest())));
    }

    [Fact]
    public void BundledPinsAreFoundBesideTheAppAndInsideTheCore()
    {
        var old = BundledModels.PinsPath;
        try
        {
            BundledModels.PinsPath = PinsPath;
            Assert.NotNull(BundledModels.Pins(ModelPinsSource.ArtifactPins));
            BundledModels.PinsPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "none", "artifact-pins.json");
            Assert.Null(BundledModels.Pins(ModelPinsSource.ArtifactPins));
        }
        finally { BundledModels.PinsPath = old; }
        Assert.NotNull(BundledModels.Pins(ModelPinsSource.WindowsModels));
        Assert.EndsWith("Murmur.Cleanup.exe", BundledModels.CleanupHelperPath);
    }
}
