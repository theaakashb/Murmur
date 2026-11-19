// Shared fixtures for the model download tests: synthetic files, a fake transport that never touches the network, a counting hasher and temp folders.
using System.Text;
using System.Text.Json;
using Murmur.Core;

namespace Murmur.Core.Tests;

internal static class ModelTestSupport
{
    public const string Repository = "Example/tiny-model";
    public const string Revision = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>Byte i is (i * 31 + seed) &amp; 0xff, as in the Swift checks.</summary>
    public static byte[] Pattern(int count, int seed = 7) => Enumerable.Range(0, count).Select(i => unchecked((byte)(i * 31 + seed))).ToArray();

    public static string ServedPath(string name) => $"/{Repository}/resolve/{Revision}/{name}";

    public static string Describe(ModelDownloadError error) => error.Kind switch
    {
        ModelDownloadErrorKind.HttpStatus => $"httpStatus:{error.Status}",
        _ => char.ToLowerInvariant(error.Kind.ToString()[0]) + error.Kind.ToString()[1..],
    };

    public static string Describe(ModelStatus status) => status.Kind switch
    {
        ModelStatusKind.Downloading => $"downloading:{status.Progress!.Value.BytesDone}/{status.Progress!.Value.TotalBytes}",
        ModelStatusKind.Corrupt => "corrupt:" + status.Reason,
        ModelStatusKind.Failed => "failed:" + Describe(status.Error!.Value),
        _ => char.ToLowerInvariant(status.Kind.ToString()[0]) + status.Kind.ToString()[1..],
    };

    /// <summary>The repository root, found from the test output folder: the shared pins live there and are read in place.</summary>
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "module-one", "models", "artifact-pins.json"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root not found");
    }

    public static string PinsPath => System.IO.Path.Combine(RepoRoot(), "module-one", "models", "artifact-pins.json");

    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "murmur-model-" + Guid.NewGuid());
        public TempDir() => Directory.CreateDirectory(Path);
        public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    public sealed record FixtureFile(string Name, byte[] Bytes, bool Lfs);

    /// <summary>A tiny model: one blob-hashed file, one nested blob-hashed file, one LFS file; plus a README that the batch list leaves out.</summary>
    public sealed class Fixture
    {
        public List<FixtureFile> Contents { get; init; } = [];
        public long Total => Contents.Sum(f => (long)f.Bytes.Length);

        public static Fixture Standard(int seed = 7) => new()
        {
            Contents =
            [
                new("config.json", Encoding.UTF8.GetBytes("{\"name\":\"tiny\"}\n"), false),
                new("enc.mlmodelc/model.mil", Pattern(9_000, seed + 1), false),
                new("enc.mlmodelc/weights/weight.bin", Pattern(70_000, seed), true),
            ],
        };

        public string Json(string[]? batch = null, Dictionary<string, string>? hashOverride = null, string revision = Revision, Dictionary<string, object>? license = null,
                           string repository = Repository, Dictionary<string, long>? lfsSizeOverride = null, Dictionary<string, long>? sizeOverride = null)
        {
            var files = Contents.Select(file =>
            {
                var entry = new Dictionary<string, object>
                {
                    ["rfilename"] = file.Name,
                    ["size"] = sizeOverride?.GetValueOrDefault(file.Name, file.Bytes.Length) ?? file.Bytes.Length,
                    ["git_blob_sha1"] = hashOverride?.GetValueOrDefault(file.Name) ?? Digests.BlobSha1Hex(file.Bytes),
                };
                if (file.Lfs) entry["lfs"] = new Dictionary<string, object> { ["sha256"] = hashOverride?.GetValueOrDefault(file.Name) ?? Digests.Sha256Hex(file.Bytes), ["size"] = lfsSizeOverride?.GetValueOrDefault(file.Name, file.Bytes.Length) ?? file.Bytes.Length, ["pointerSize"] = 130 };
                return entry;
            }).Append(new Dictionary<string, object> { ["rfilename"] = "README.md", ["size"] = 5, ["git_blob_sha1"] = new string('a', 40) }).ToList();
            var model = new Dictionary<string, object> { ["revision"] = revision, ["files"] = files, ["batch_files"] = batch ?? Contents.Select(f => f.Name).ToArray() };
            if (license is not null) model["license"] = license;
            return JsonSerializer.Serialize(new Dictionary<string, object> { ["models"] = new Dictionary<string, object> { [repository] = model } });
        }

        public ModelManifest Manifest(Dictionary<string, string>? hashOverride = null) => new(Json(hashOverride: hashOverride), Repository);

        public void Serve(FakeTransport transport)
        {
            foreach (var file in Contents) transport.Files[ServedPath(file.Name)] = file.Bytes;
        }
    }

    /// <summary>Serves fixture bytes by URL path. Honors Range (206) unless <see cref="IgnoresRange"/>; records every request.</summary>
    public sealed class FakeTransport : IModelDownloadTransport
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public Dictionary<string, int> StatusOverride { get; set; } = [];
        /// <summary>Simulates a dropped connection.</summary>
        public Dictionary<string, int> FailAfterBytes { get; set; } = [];
        public bool IgnoresRange { get; set; }
        public int ChunkSize { get; set; } = 4096;
        /// <summary>(path, bytes delivered so far in this request)</summary>
        public Action<string, int>? OnChunk { get; set; }
        public List<ModelFetchRequest> Requests { get; } = [];

        public void Fetch(ModelFetchRequest request, Func<bool> isCancelled, Action<int> onResponse, Action<ReadOnlyMemory<byte>> onData)
        {
            Requests.Add(request);
            var path = request.Url.AbsolutePath;
            if (isCancelled()) throw new ModelDownloadException(ModelDownloadError.Cancelled);
            if (StatusOverride.TryGetValue(path, out var status)) { onResponse(status); return; }
            if (!Files.TryGetValue(path, out var body)) { onResponse(404); return; }
            var start = 0;
            if (request.RangeStart > 0 && !IgnoresRange)
            {
                if (request.RangeStart >= body.Length) { onResponse(416); return; }
                start = (int)request.RangeStart;
                onResponse(206);
            }
            else onResponse(200);
            var delivered = 0;
            var offset = start;
            while (offset < body.Length)
            {
                if (isCancelled()) throw new ModelDownloadException(ModelDownloadError.Cancelled);
                if (FailAfterBytes.TryGetValue(path, out var limit) && delivered >= limit) throw new ModelDownloadException(ModelDownloadError.Network);
                var end = Math.Min(body.Length, offset + ChunkSize);
                onData(body.AsMemory(offset, end - offset));
                delivered += end - offset;
                offset = end;
                OnChunk?.Invoke(path, delivered);
            }
        }
    }

    /// <summary>Wraps the real hasher, recording the file names it was asked about.</summary>
    public sealed class CountingHasher : IFileHasher
    {
        public List<string> Calls { get; } = [];
        public Action? Observe { get; set; }
        readonly StreamingFileHasher inner = new(1000);
        public string HexDigest(string file, ModelHashAlgorithm algorithm, Func<bool> isCancelled)
        {
            Calls.Add(System.IO.Path.GetFileName(file));
            Observe?.Invoke();
            return inner.HexDigest(file, algorithm, isCancelled);
        }
    }

    /// <summary>"relative/path:size:sha256 prefix" for every file under <paramref name="root"/>, sorted.</summary>
    public static List<string> ListFiles(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (Name: System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'), File: f))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => $"{x.Name}:{new FileInfo(x.File).Length}:{Digests.Sha256Hex(File.ReadAllBytes(x.File))[..16]}").ToList();
    }
}
