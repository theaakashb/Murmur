// Compares Murmur.Core with golden/model_download.json, which windows/golden/golden_model_download.swift writes from the Mac's Swift.
using System.Text;
using System.Text.Json;
using Murmur.Core;
using static Murmur.Core.Tests.ModelTestSupport;

namespace Murmur.Core.Tests;

public class ModelDownloadGoldenTests
{
    static readonly JsonElement Root = GoldenFiles.Load("model_download");
    static JsonElement Case(string section, int i) => Root.GetProperty(section)[i];

    public static IEnumerable<object[]> Digest => GoldenFiles.Cases(Root, "digests", c => $"{c.GetProperty("kind").GetString()} {GoldenFiles.Optional(c, "text")} {(c.TryGetProperty("count", out var n) ? n : "")}");
    public static IEnumerable<object[]> Manifests => GoldenFiles.Cases(Root, "manifests", c => c.GetProperty("label").GetString()!);
    public static IEnumerable<object[]> Paths => GoldenFiles.Cases(Root, "paths", c => c.GetProperty("path").GetString()!);
    public static IEnumerable<object[]> Repos => GoldenFiles.Cases(Root, "repos", c => c.GetProperty("repository").GetString()!);
    public static IEnumerable<object[]> Hex => GoldenFiles.Cases(Root, "hex", c => c.GetProperty("value").GetString()!);
    public static IEnumerable<object[]> Messages => GoldenFiles.Cases(Root, "messages", c => c.GetProperty("error").GetString()!);
    public static IEnumerable<object[]> Scenarios => GoldenFiles.Cases(Root, "scenarios", c => c.GetProperty("name").GetString()!);

    static byte[] Input(JsonElement c) => c.GetProperty("kind").GetString() switch
    {
        "text" => Encoding.UTF8.GetBytes(c.GetProperty("text").GetString()!),
        "pattern" => Pattern(c.GetProperty("count").GetInt32(), c.GetProperty("seed").GetInt32()),
        _ => Enumerable.Repeat((byte)c.GetProperty("byte").GetInt32(), c.GetProperty("count").GetInt32()).ToArray(),
    };

    [Theory, MemberData(nameof(Digest))]
    public void DigestsMatch(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("digests", i);
        var data = Input(c);
        Assert.Equal(c.GetProperty("sha256").GetString(), Digests.Sha256Hex(data));
        Assert.Equal(c.GetProperty("sha1").GetString(), Digests.Sha1Hex(data));
        Assert.Equal(c.GetProperty("blobSha1").GetString(), Digests.BlobSha1Hex(data));
    }

    [Theory, MemberData(nameof(Manifests))]
    public void ManifestMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("manifests", i);
        var repository = c.GetProperty("repository").GetString()!;
        var list = c.GetProperty("list").GetString()!;
        ModelManifest? manifest = null;
        ModelDownloadError? error = null;
        try
        {
            manifest = c.TryGetProperty("path", out var path) ? ModelManifest.Load(System.IO.Path.Combine(RepoRoot(), path.GetString()!), repository, list) : new ModelManifest(c.GetProperty("json").GetString()!, repository, list);
        }
        catch (ModelDownloadException e) { error = e.Error; }
        if (c.TryGetProperty("error", out var expectedError))
        {
            Assert.Equal(expectedError.GetString(), error is { } e ? Describe(e) : null);
            return;
        }
        Assert.Null(error);
        var expected = c.GetProperty("manifest");
        Assert.Equal(expected.GetProperty("revision").GetString(), manifest!.Revision);
        Assert.Equal(expected.GetProperty("total").GetInt64(), manifest.TotalBytes);
        var files = expected.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(files.Count, manifest.Files.Count);
        for (var n = 0; n < files.Count; n++)
        {
            var file = manifest.Files[n];
            Assert.Equal(files[n].GetProperty("name").GetString(), file.Rfilename);
            Assert.Equal(files[n].GetProperty("size").GetInt64(), file.Size);
            Assert.Equal(files[n].GetProperty("checksum").GetString(), file.Checksum);
            Assert.Equal(files[n].GetProperty("algorithm").GetString(), file.Algorithm == ModelHashAlgorithm.Sha256 ? "sha256" : "blobSha1");
            Assert.Equal(files[n].GetProperty("url").GetString(), manifest.ResolveUrl(file)!.AbsoluteUri);
        }
        var license = expected.GetProperty("license");
        Assert.Equal(license.GetProperty("pinned").GetBoolean(), manifest.License.IsPinned);
        Assert.Equal(license.GetProperty("note").GetString(), manifest.License.Note);
        Assert.Equal(license.GetProperty("declarations").EnumerateArray().Select(d => string.Join("|", d.EnumerateArray().Select(x => x.GetString()))),
                     manifest.License.Declarations.Select(d => $"{d.Source}|{d.Name}|{d.Url.AbsoluteUri}"));
    }

    [Theory, MemberData(nameof(Paths))]
    public void SafePathMatches(int i, string label) { Assert.NotNull(label); var c = Case("paths", i); Assert.Equal(c.GetProperty("safe").GetBoolean(), ModelManifest.IsSafeRelativePath(c.GetProperty("path").GetString()!)); }

    [Theory, MemberData(nameof(Repos))]
    public void SafeRepositoryMatches(int i, string label) { Assert.NotNull(label); var c = Case("repos", i); Assert.Equal(c.GetProperty("safe").GetBoolean(), ModelManifest.IsSafeRepository(c.GetProperty("repository").GetString()!)); }

    [Theory, MemberData(nameof(Hex))]
    public void HexMatches(int i, string label) { Assert.NotNull(label); var c = Case("hex", i); Assert.Equal(c.GetProperty("hex").GetBoolean(), ModelManifest.IsHex(c.GetProperty("value").GetString()!, c.GetProperty("length").GetInt32())); }

    [Theory, MemberData(nameof(Messages))]
    public void MessageMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("messages", i);
        var name = c.GetProperty("error").GetString()!;
        var error = name.StartsWith("httpStatus:") ? ModelDownloadError.HttpStatus(int.Parse(name[11..])) : new ModelDownloadError(Enum.Parse<ModelDownloadErrorKind>(name, ignoreCase: true));
        Assert.Equal(c.GetProperty("message").GetString(), error.UserMessage);
        Assert.Equal(name, Describe(error));
    }

    /// <summary>The state machine: each attempt is one install (or remove) against a fake transport; the requests, progress, hashing and files on disk match what the Swift code did.</summary>
    [Theory, MemberData(nameof(Scenarios))]
    public void ScenarioMatches(int i, string label)
    {
        Assert.NotNull(label);
        var c = Case("scenarios", i);
        using var temp = new TempDir();
        var target = temp.Combine("model");
        var attempts = c.GetProperty("attempts").EnumerateArray().ToList();
        var results = c.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(attempts.Count, results.Count);
        for (var n = 0; n < attempts.Count; n++)
        {
            var a = attempts[n];
            var expected = results[n];
            var fixture = Fixture.Standard(a.TryGetProperty("seed", out var seed) ? seed.GetInt32() : 7);
            var transport = new FakeTransport();
            fixture.Serve(transport);
            foreach (var (name, index) in Map(a, "corrupt")) { var data = (byte[])transport.Files[ServedPath(name)].Clone(); data[(int)index] ^= 0xff; transport.Files[ServedPath(name)] = data; }
            if (a.TryGetProperty("extra", out var extra)) foreach (var name in extra.EnumerateArray()) transport.Files[ServedPath(name.GetString()!)] = [.. transport.Files[ServedPath(name.GetString()!)], .. "extra"u8];
            transport.IgnoresRange = a.TryGetProperty("ignoresRange", out var ignores) && ignores.GetBoolean();
            transport.FailAfterBytes = Map(a, "failAfter").ToDictionary(x => ServedPath(x.Key), x => (int)x.Value);
            transport.StatusOverride = Map(a, "statusOverride").ToDictionary(x => ServedPath(x.Key), x => (int)x.Value);
            var hasher = new CountingHasher();
            long? free = a.TryGetProperty("free", out var f) ? f.GetInt64() : null;
            var downloader = new ModelDownloader(fixture.Manifest(), target, transport, hasher, freeSpace: _ => free);
            var cancelAt = Map(a, "cancelAt").ToDictionary(x => ServedPath(x.Key), x => (int)x.Value);
            if (cancelAt.Count > 0) transport.OnChunk = (p, delivered) => { if (cancelAt.TryGetValue(p, out var limit) && delivered >= limit) downloader.Cancel(); };
            var staging = downloader.StagingDirectory;
            if (Flag(a, "strayInStaging"))
            {
                Directory.CreateDirectory(System.IO.Path.Combine(staging, "junk"));
                File.WriteAllText(System.IO.Path.Combine(staging, "junk", "evil.bin"), "x");
                File.WriteAllText(System.IO.Path.Combine(staging, "extra.json"), "x");
            }
            if (Flag(a, "alteredConfigInStaging")) { Directory.CreateDirectory(staging); File.WriteAllText(System.IO.Path.Combine(staging, "config.json"), "{\"name\":\"evil\"}\n"); }
            var progress = new List<long>();
            string? error = null;
            try { if (Flag(a, "remove")) downloader.Remove(); else downloader.Install(p => progress.Add(p.BytesDone)); }
            catch (ModelDownloadException e) { error = Describe(e.Error); }
            var where = $"{c.GetProperty("name").GetString()} attempt {n}";
            Assert.True(GoldenFiles.Optional(expected, "error") == error, $"{where}: error {error}");
            Assert.Equal(expected.GetProperty("status").GetString(), Describe(downloader.Status()));
            Assert.Equal(expected.GetProperty("requests").EnumerateArray().Select(r => r[0].GetInt64()), transport.Requests.Select(r => r.RangeStart));
            Assert.Equal(expected.GetProperty("paths").EnumerateArray().Select(p => p.GetString()), transport.Requests.Select(r => r.Url.AbsolutePath[$"/{Repository}/resolve/{Revision}/".Length..]));
            Assert.Equal(expected.GetProperty("progress").EnumerateArray().Select(p => p.GetInt64()), progress);
            Assert.Equal(expected.GetProperty("hashed").EnumerateArray().Select(p => p.GetString()), hasher.Calls);
            Assert.Equal(expected.GetProperty("files").EnumerateArray().Select(p => p.GetString()), ListFiles(target));
            Assert.Equal(expected.GetProperty("stagingFiles").EnumerateArray().Select(p => p.GetString()), ListFiles(staging));
            var partial = System.IO.Path.Combine(staging, "enc.mlmodelc", "weights", "weight.bin.part");
            Assert.Equal(expected.GetProperty("partialWeight").GetInt32(), File.Exists(partial) ? (int)new FileInfo(partial).Length : -1);
            if (Flag(a, "verifyAfter")) Assert.Equal(expected.GetProperty("verified").GetString(), Describe(downloader.VerifyInstalled()));
        }
    }

    static bool Flag(JsonElement a, string name) => a.TryGetProperty(name, out var v) && v.GetBoolean();
    static IEnumerable<KeyValuePair<string, long>> Map(JsonElement a, string name) =>
        a.TryGetProperty(name, out var m) ? m.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value.GetInt64())) : [];
}
