// Port of app/ModelDownload.swift.
// Downloads the pinned model files from Hugging Face, verifies every byte against the pins, and moves the verified set into place with one rename.
namespace Murmur.Core;

public enum ModelDownloadErrorKind { Network, DiskFull, ChecksumMismatch, HttpStatus, Cancelled, InvalidManifest, UnsafePath, FileSystem, Busy }

/// <summary>Every case maps to a short, user-facing message that names no file, path, or URL.</summary>
public readonly record struct ModelDownloadError(ModelDownloadErrorKind Kind, int Status = 0)
{
    public static readonly ModelDownloadError Network = new(ModelDownloadErrorKind.Network);
    public static readonly ModelDownloadError DiskFull = new(ModelDownloadErrorKind.DiskFull);
    public static readonly ModelDownloadError ChecksumMismatch = new(ModelDownloadErrorKind.ChecksumMismatch);
    public static readonly ModelDownloadError Cancelled = new(ModelDownloadErrorKind.Cancelled);
    public static readonly ModelDownloadError InvalidManifest = new(ModelDownloadErrorKind.InvalidManifest);
    public static readonly ModelDownloadError UnsafePath = new(ModelDownloadErrorKind.UnsafePath);
    public static readonly ModelDownloadError FileSystem = new(ModelDownloadErrorKind.FileSystem);
    public static readonly ModelDownloadError Busy = new(ModelDownloadErrorKind.Busy);
    public static ModelDownloadError HttpStatus(int code) => new(ModelDownloadErrorKind.HttpStatus, code);

    public string UserMessage => Kind switch
    {
        ModelDownloadErrorKind.Network => "The model download could not reach the server. Check your internet connection and try again.",
        ModelDownloadErrorKind.DiskFull => "There is not enough free disk space for the model. Free up space and try again.",
        ModelDownloadErrorKind.ChecksumMismatch => "A downloaded model file did not match its pinned checksum and was discarded. Try the download again.",
        ModelDownloadErrorKind.HttpStatus => $"The model server returned an error (HTTP {Status}). Try again later.",
        ModelDownloadErrorKind.Cancelled => "The model download was cancelled.",
        ModelDownloadErrorKind.InvalidManifest => "The pinned model manifest is missing or invalid. Reinstall Murmur.",
        ModelDownloadErrorKind.UnsafePath => "The pinned model manifest contains an unsafe file path, so nothing was downloaded.",
        ModelDownloadErrorKind.FileSystem => "Murmur could not write the model files. Check that the model folder is writable.",
        ModelDownloadErrorKind.Busy => "A model download is already in progress.",
        _ => throw new System.Diagnostics.UnreachableException(),
    };

    /// <summary>Maps file-write failures; a full disk (Windows ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL, or ENOSPC / EDQUOT) becomes <see cref="DiskFull"/>.</summary>
    public static ModelDownloadError FromWrite(Exception error)
    {
        if (error is ModelDownloadException known) return known.Error;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            var hr = current.HResult;
            if ((hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? (hr & 0xFFFF) is 0x70 or 0x27 : hr is 28 or 69 or 122) return DiskFull;
        }
        return FileSystem;
    }
}

public sealed class ModelDownloadException(ModelDownloadError error) : Exception(error.UserMessage)
{
    public ModelDownloadError Error { get; } = error;
}

/// <summary>One GET. <see cref="RangeStart"/> &gt; 0 asks the server for <c>Range: bytes=&lt;RangeStart&gt;-</c>.</summary>
public sealed record ModelFetchRequest(Uri Url, long RangeStart);

/// <summary>Blocking, streaming HTTP GET. Implementations follow redirects, call <c>onResponse</c> once with the final status code before any body bytes, then
/// <c>onData</c> in order. If a callback throws, stop and rethrow that exception. Poll <c>isCancelled</c> and throw <see cref="ModelDownloadError.Cancelled"/> when it is true.
/// Transport failures are thrown as <see cref="ModelDownloadError.Network"/>.</summary>
public interface IModelDownloadTransport
{
    void Fetch(ModelFetchRequest request, Func<bool> isCancelled, Action<int> onResponse, Action<ReadOnlyMemory<byte>> onData);
}

/// <summary>HttpClient-backed transport: no cookies, no cache, 60 s without a byte is a network failure.</summary>
public sealed class HttpModelTransport : IModelDownloadTransport
{
    static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    readonly HttpClient client;

    public HttpModelTransport(HttpMessageHandler? handler = null) =>
        client = new HttpClient(handler ?? new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };

    public void Fetch(ModelFetchRequest request, Func<bool> isCancelled, Action<int> onResponse, Action<ReadOnlyMemory<byte>> onData)
    {
        using var stop = new CancellationTokenSource();
        using var watcher = new Timer(_ => { if (isCancelled()) stop.Cancel(); }, null, 100, 100);
        try { Run(request, stop, isCancelled, onResponse, onData).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { throw new ModelDownloadException(isCancelled() ? ModelDownloadError.Cancelled : ModelDownloadError.Network); }
        catch (Exception e) when (e is HttpRequestException or IOException) { throw new ModelDownloadException(isCancelled() ? ModelDownloadError.Cancelled : ModelDownloadError.Network); }
    }

    async Task Run(ModelFetchRequest request, CancellationTokenSource stop, Func<bool> isCancelled, Action<int> onResponse, Action<ReadOnlyMemory<byte>> onData)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
        message.Headers.CacheControl = new() { NoCache = true };
        if (request.RangeStart > 0) message.Headers.Range = new(request.RangeStart, null);
        using var first = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        first.CancelAfter(IdleTimeout);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, first.Token).ConfigureAwait(false);
        onResponse((int)response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
        var buffer = new byte[81920];
        while (true)
        {
            if (isCancelled()) throw new ModelDownloadException(ModelDownloadError.Cancelled);
            using var step = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            step.CancelAfter(IdleTimeout);
            var read = await body.ReadAsync(buffer, step.Token).ConfigureAwait(false);
            if (read == 0) return;
            onData(buffer.AsMemory(0, read));
        }
    }
}

public readonly record struct ModelDownloadProgress(long BytesDone, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? Math.Min(1, (double)BytesDone / TotalBytes) : 0;
}

public enum ModelStatusKind { NotDownloaded, Downloading, Verifying, Ready, Corrupt, Failed }

public sealed record ModelStatus(ModelStatusKind Kind, ModelDownloadProgress? Progress = null, string? Reason = null, ModelDownloadError? Error = null)
{
    public static readonly ModelStatus NotDownloaded = new(ModelStatusKind.NotDownloaded);
    public static readonly ModelStatus Verifying = new(ModelStatusKind.Verifying);
    public static readonly ModelStatus Ready = new(ModelStatusKind.Ready);
    public static ModelStatus Downloading(ModelDownloadProgress progress) => new(ModelStatusKind.Downloading, Progress: progress);
    /// <summary><paramref name="reason"/> is user-facing and content-free.</summary>
    public static ModelStatus Corrupt(string reason) => new(ModelStatusKind.Corrupt, Reason: reason);
    public static ModelStatus Failed(ModelDownloadError error) => new(ModelStatusKind.Failed, Error: error);
}

/// <summary>Downloads exactly the pinned files into a staging folder beside <see cref="Target"/>, verifies each file's size and checksum, then renames the staging
/// folder onto <see cref="Target"/>. The target is never partially written: until the final rename it holds either nothing or the previous model.
/// <see cref="Install"/> blocks; call it off the UI thread (or use <see cref="Start"/>). <see cref="Cancel"/>, <see cref="Status"/> and <see cref="License"/> are safe from any thread.</summary>
public sealed class ModelDownloader
{
    public const string CorruptReason = "Some model files are missing or damaged. Remove the model and download it again.";
    public const string NotDirectoryReason = "The model location is not a folder. Remove the model and download it again.";

    public ModelManifest Manifest { get; }
    public string Target { get; }
    readonly IModelDownloadTransport transport;
    readonly IFileHasher hasher;
    readonly string baseUrl;
    readonly Func<string, long?> freeSpace;

    readonly object gate = new();
    ModelStatus? active;
    ModelDownloadError? lastFailure;
    bool cancelRequested;
    bool running;
    Action<ModelDownloadProgress>? progressCallback;
    ModelDownloadProgress? lastPublished;

    /// <param name="target">final model folder; its parent must be on the volume where staging is created.</param>
    /// <param name="freeSpace">available bytes on the volume holding a path; null skips the preflight.</param>
    public ModelDownloader(ModelManifest manifest, string target, IModelDownloadTransport? transport = null, IFileHasher? hasher = null,
                           string baseUrl = "https://huggingface.co", Func<string, long?>? freeSpace = null)
    {
        Manifest = manifest;
        Target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        this.transport = transport ?? new HttpModelTransport();
        this.hasher = hasher ?? new StreamingFileHasher();
        this.baseUrl = baseUrl;
        this.freeSpace = freeSpace ?? SystemFreeSpace;
    }

    public ModelLicenseInfo License => Manifest.License;
    public long DownloadSize => Manifest.TotalBytes;

    /// <summary>Hidden-style sibling of <see cref="Target"/>, keyed by revision so a retry resumes the same pinned set: <c>.&lt;name&gt;.download-&lt;rev12&gt;</c>.</summary>
    public string StagingDirectory => Path.Combine(Path.GetDirectoryName(Target)!, $".{Path.GetFileName(Target)}.download-{Manifest.Revision[..12]}");

    public static long? SystemFreeSpace(string path)
    {
        try
        {
            var probe = Path.GetFullPath(path);
            while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { } parent) probe = parent;
            return new DriveInfo(probe).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>Cheap status: in-flight state, then the last failure, then an on-disk size check (no hashing). Use <see cref="VerifyInstalled"/> for a full checksum pass.</summary>
    public ModelStatus Status()
    {
        lock (gate)
        {
            if (active is not null) return active;
            if (lastFailure is { } failure) return ModelStatus.Failed(failure);
        }
        return DiskStatus(hash: false, () => false);
    }

    /// <summary>Full size + checksum check of the installed model. Does not change <see cref="Status"/> state.</summary>
    public ModelStatus VerifyInstalled(Func<bool>? isCancelled = null) => DiskStatus(hash: true, isCancelled ?? (() => false));

    /// <summary>Requests cancellation; <see cref="Install"/> throws <see cref="ModelDownloadError.Cancelled"/> at the next chunk. Already-verified staged files are kept so a later install can skip them; <see cref="Remove"/> discards them.</summary>
    public void Cancel() { lock (gate) { if (running) cancelRequested = true; } }

    /// <summary>Runs <see cref="Install"/> on a worker thread. <paramref name="completion"/> gets null on success; callbacks arrive on that thread.</summary>
    public void Start(Action<ModelDownloadProgress>? progress, Action<ModelDownloadError?> completion) =>
        Task.Run(() =>
        {
            ModelDownloadError? result = null;
            try { Install(progress); } catch (ModelDownloadException e) { result = e.Error; } catch (Exception) { result = ModelDownloadError.FileSystem; }
            completion(result);
        });

    /// <summary>Downloads (or resumes) the pinned files, verifies all of them, then installs atomically.
    /// Throws <see cref="ModelDownloadException"/>. An existing target is replaced only after every file verifies.</summary>
    public void Install(Action<ModelDownloadProgress>? progress = null)
    {
        lock (gate)
        {
            if (running) throw new ModelDownloadException(ModelDownloadError.Busy);
            running = true; cancelRequested = false; lastFailure = null; progressCallback = progress; lastPublished = null;
            active = ModelStatus.Downloading(new(0, Manifest.TotalBytes));
        }
        try
        {
            PerformInstall();
            Finish(null);
        }
        catch (Exception e)
        {
            var classified = ModelDownloadError.FromWrite(e);
            Finish(classified);
            throw new ModelDownloadException(classified);
        }
    }

    /// <summary>Deletes the installed model and any staged partial download. Refuses while a download runs.</summary>
    public void Remove()
    {
        lock (gate)
        {
            if (running) throw new ModelDownloadException(ModelDownloadError.Busy);
            lastFailure = null;
        }
        foreach (var path in new[] { Target, StagingDirectory })
        {
            if (!Directory.Exists(path) && !File.Exists(path) && !FileLinks.IsLink(path)) continue;
            try { DeleteEntry(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ModelDownloadException(ModelDownloadError.FileSystem); }
        }
    }

    bool IsCancelled { get { lock (gate) return cancelRequested; } }

    void SetActive(ModelStatus status) { lock (gate) active = status; }

    void Publish(ModelDownloadProgress value)
    {
        Action<ModelDownloadProgress>? callback;
        bool changed;
        lock (gate)
        {
            changed = lastPublished != value;
            lastPublished = value;
            active = ModelStatus.Downloading(value);
            callback = progressCallback;
        }
        if (changed) callback?.Invoke(value);
    }

    void Finish(ModelDownloadError? failure)
    {
        lock (gate) { running = false; active = null; lastFailure = failure; cancelRequested = false; progressCallback = null; }
    }

    void CheckCancelled() { if (IsCancelled) throw new ModelDownloadException(ModelDownloadError.Cancelled); }

    /// <summary>Removes a file, a folder or a link itself (never what a link points to).</summary>
    static void DeleteEntry(string path)
    {
        if (FileLinks.IsLink(path)) FileLinks.RemoveLink(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }

    string Location(ModelManifestFile file, string root)
    {
        if (!ModelManifest.IsSafeRelativePath(file.Rfilename)) throw new ModelDownloadException(ModelDownloadError.UnsafePath);
        var full = Path.GetFullPath(Path.Combine(root, file.Rfilename.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ModelDownloadException(ModelDownloadError.UnsafePath);
        return full;
    }

    bool Verify(ModelManifestFile file, string path)
    {
        if (Digests.FileSize(path) != file.Size) return false;
        return hasher.HexDigest(path, file.Algorithm, () => IsCancelled) == file.Checksum;
    }

    ModelStatus DiskStatus(bool hash, Func<bool> isCancelled)
    {
        if (!Directory.Exists(Target) && !File.Exists(Target) && !FileLinks.IsLink(Target)) return ModelStatus.NotDownloaded;
        if (!Directory.Exists(Target) || FileLinks.IsLink(Target)) return ModelStatus.Corrupt(NotDirectoryReason);
        foreach (var file in Manifest.Files)
        {
            string path;
            try { path = Location(file, Target); } catch (ModelDownloadException) { return ModelStatus.Corrupt(CorruptReason); }
            if (Digests.FileSize(path) != file.Size) return ModelStatus.Corrupt(CorruptReason);
            if (!hash) continue;
            try { if (hasher.HexDigest(path, file.Algorithm, isCancelled) != file.Checksum) return ModelStatus.Corrupt(CorruptReason); }
            catch (ModelDownloadException) { return ModelStatus.Corrupt(CorruptReason); }
        }
        return ModelStatus.Ready;
    }

    void PerformInstall()
    {
        var staging = StagingDirectory;
        var total = Manifest.TotalBytes;
        long done = 0;
        void Report(long bytes) => Publish(new(bytes, total));
        var plan = Manifest.Files.Select(file =>
            (File: file, Destination: Location(file, staging), Url: Manifest.ResolveUrl(file, baseUrl) ?? throw new ModelDownloadException(ModelDownloadError.InvalidManifest))).ToList();
        if (FileLinks.IsLink(staging)) { try { DeleteEntry(staging); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        try { Directory.CreateDirectory(staging); } catch (Exception e) { throw new ModelDownloadException(ModelDownloadError.FromWrite(e)); }

        // Preflight: remaining bytes against free space (only counts final, not yet verified, files).
        var staged = plan.Sum(item => Digests.FileSize(item.Destination) ?? Digests.FileSize(item.Destination + ".part") ?? 0);
        if (freeSpace(staging) is { } free && free < total - staged) throw new ModelDownloadException(ModelDownloadError.DiskFull);

        Report(done);
        foreach (var (file, destination, url) in plan)
        {
            CheckCancelled();
            var partial = destination + ".part";
            try { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); } catch (Exception e) { throw new ModelDownloadException(ModelDownloadError.FromWrite(e)); }
            // A final-named staged file was verified when it was downloaded; re-hash before trusting it.
            if (File.Exists(destination))
            {
                SetActive(ModelStatus.Verifying);
                if (Verify(file, destination)) { done += file.Size; Report(done); continue; }
                TryDelete(destination);
            }
            done = Download(file, url, partial, done, Report);
            SetActive(ModelStatus.Verifying);
            if (!Verify(file, partial))
            {
                TryDelete(partial);
                throw new ModelDownloadException(ModelDownloadError.ChecksumMismatch);
            }
            try { File.Move(partial, destination); } catch (Exception e) { throw new ModelDownloadException(ModelDownloadError.FromWrite(e)); }
            Report(done);
        }
        CheckCancelled();
        SetActive(ModelStatus.Verifying);
        PruneUnexpected(staging, plan.Select(item => item.File.Rfilename).ToHashSet());
        CheckCancelled();
        SwapIntoPlace(staging);
    }

    static void TryDelete(string path) { try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }

    /// <summary>Streams one file into <paramref name="partial"/>, resuming from its current length when the server honors Range.</summary>
    long Download(ModelManifestFile file, Uri url, string partial, long doneBefore, Action<long> report)
    {
        var offset = Digests.FileSize(partial) ?? 0;
        if (offset > file.Size) { TryDelete(partial); offset = 0; }
        if (offset == file.Size) { report(doneBefore + offset); return doneBefore + offset; }   // complete but unverified
        report(doneBefore + offset);
        FileStream handle;
        try
        {
            if (offset == 0) { TryDelete(partial); handle = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.Read); }
            else handle = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.Read);
        }
        catch (Exception e) { throw new ModelDownloadException(ModelDownloadError.FromWrite(e)); }
        using (handle)
        {
            long written = 0, baseOffset = offset, latest = doneBefore + offset;
            var started = false;
            try
            {
                transport.Fetch(new ModelFetchRequest(url, offset), () => IsCancelled, status =>
                {
                    switch (status)
                    {
                        case 206 when baseOffset > 0:
                            handle.Seek(0, SeekOrigin.End);
                            break;
                        case 200:
                            // Server ignored Range (or none was sent): restart the file from zero.
                            handle.SetLength(0);
                            baseOffset = 0;
                            break;
                        default:
                            if (status == 416 || (status == 206 && baseOffset == 0)) { try { handle.SetLength(0); } catch (IOException) { } baseOffset = 0; }
                            throw new ModelDownloadException(ModelDownloadError.HttpStatus(status));
                    }
                    started = true;
                }, data =>
                {
                    if (!started) throw new ModelDownloadException(ModelDownloadError.Network);
                    if (IsCancelled) throw new ModelDownloadException(ModelDownloadError.Cancelled);
                    if (baseOffset + written + data.Length > file.Size) throw new ModelDownloadException(ModelDownloadError.ChecksumMismatch);
                    try { handle.Write(data.Span); } catch (Exception e) { throw new ModelDownloadException(ModelDownloadError.FromWrite(e)); }
                    written += data.Length;
                    latest = doneBefore + baseOffset + written;
                    Publish(new(latest, Manifest.TotalBytes));
                });
            }
            catch (ModelDownloadException e)
            {
                try { handle.Flush(true); } catch (IOException) { }
                if (e.Error == ModelDownloadError.ChecksumMismatch) { handle.Dispose(); TryDelete(partial); }
                throw;
            }
            catch (Exception e) { throw new ModelDownloadException(ModelDownloadError.FromWrite(e)); }
            try { handle.Flush(true); } catch (Exception e) { throw new ModelDownloadException(ModelDownloadError.FromWrite(e)); }
            if (baseOffset + written != file.Size) throw new ModelDownloadException(ModelDownloadError.Network);   // truncated body
            return latest;
        }
    }

    /// <summary>Deletes anything in staging that is not a pinned file (or a folder leading to one), so no stray or substituted file can be installed alongside the model.</summary>
    void PruneUnexpected(string staging, HashSet<string> expected)
    {
        var extras = new List<string>();
        void Walk(string directory, string prefix)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = prefix + Path.GetFileName(entry);
                if (Directory.Exists(entry) && !FileLinks.IsLink(entry))
                {
                    if (!expected.Any(e => e.StartsWith(relative + "/", StringComparison.Ordinal))) extras.Add(entry);
                    else Walk(entry, relative + "/");
                }
                else if (!expected.Contains(relative)) extras.Add(entry);
            }
        }
        try
        {
            Walk(staging, "");
            foreach (var path in extras) DeleteEntry(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ModelDownloadException(ModelDownloadError.FileSystem); }
    }

    /// <summary>Renames staging onto target. With no existing target this is a single rename; otherwise the old model is renamed aside (<c>.&lt;name&gt;.old-&lt;uuid&gt;</c>) first and restored if the second rename fails.</summary>
    void SwapIntoPlace(string staging)
    {
        var parent = Path.GetDirectoryName(Target)!;
        if (Directory.Exists(Target) || File.Exists(Target) || FileLinks.IsLink(Target))
        {
            var aside = Path.Combine(parent, $".{Path.GetFileName(Target)}.old-{Guid.NewGuid()}");
            try { Move(Target, aside); } catch (Exception e) { throw new ModelDownloadException(ModelDownloadError.FromWrite(e)); }
            try { Directory.Move(staging, Target); }
            catch (Exception e)
            {
                try { Move(aside, Target); } catch (Exception restore) when (restore is IOException or UnauthorizedAccessException) { }
                throw new ModelDownloadException(ModelDownloadError.FromWrite(e));
            }
            try { DeleteEntry(aside); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        else
        {
            try { Directory.Move(staging, Target); } catch (Exception e) { throw new ModelDownloadException(ModelDownloadError.FromWrite(e)); }
        }
    }

    static void Move(string from, string to)
    {
        if (Directory.Exists(from) || FileLinks.IsLink(from)) Directory.Move(from, to); else File.Move(from, to);
    }
}
