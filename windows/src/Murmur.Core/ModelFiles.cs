// Port of app/ModelFiles.swift.
// Verifying the pinned model files on disk (sizes, checksums, a per-launch cache) and where the bundled pins and helper live.
using System.Reflection;

namespace Murmur.Core;

/// <summary>The one place that finds the pins manifest. Windows: <c>artifact-pins.json</c> must be copied beside Murmur.exe by the app build (not wired yet: until it is, <see cref="Pins"/> returns null for <see cref="ModelPinsSource.ArtifactPins"/>); the Windows-only manifest is embedded in Murmur.Core.</summary>
public static class BundledModels
{
    /// <summary>Overridable for tests and for a portable layout.</summary>
    public static string PinsPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "artifact-pins.json");

    /// <summary>The cleanup helper (Murmur.Cleanup, built from windows/src/Murmur.Cleanup) beside Murmur.exe. Swift: <c>Bundle.main.url(forAuxiliaryExecutable:)</c>.</summary>
    public static string CleanupHelperPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Murmur.Cleanup.exe");

    /// <summary>The bytes of the manifest a model's pins live in; null when the file is missing.</summary>
    public static byte[]? Pins(ModelPinsSource source)
    {
        if (source == ModelPinsSource.WindowsModels)
        {
            using var stream = typeof(BundledModels).Assembly.GetManifestResourceStream("shared/windows-models.json");
            if (stream is null) return null;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        try { return File.ReadAllBytes(PinsPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Hashing the 1.8 GB model took about 0.9 s before every dictation on the Mac. Verify once, then reuse the result while every pinned file keeps the same size,
/// modification time and creation time (the Mac also uses the inode, which Windows does not give cheaply).
/// shortcut: a same-user writer that restores size and both times evades this; that writer could also replace the app itself. Re-hash per dictation if that threat model changes.</summary>
public sealed class ModelVerificationCache
{
    public static readonly ModelVerificationCache Shared = new();
    readonly object gate = new();
    /// <summary>Verified fingerprint per folder.</summary>
    readonly Dictionary<string, string[]> verified = [];

    public void Verify(string directory, ModelManifest manifest)
    {
        var fingerprint = Fingerprint(directory, manifest);
        lock (gate) { if (verified.TryGetValue(directory, out var known) && known.SequenceEqual(fingerprint)) return; }
        ModelFiles.VerifyAppModels(directory, manifest);
        lock (gate) verified[directory] = fingerprint;
    }

    /// <summary>Forgets a folder (after a remove or a download), so the next use hashes again.</summary>
    public void Forget(string directory) { lock (gate) verified.Remove(directory); }

    public static string[] Fingerprint(string directory, ModelManifest manifest) =>
        manifest.Files.Select(file =>
        {
            var info = new FileInfo(Path.Combine(directory, file.Rfilename.Replace('/', Path.DirectorySeparatorChar)));
            if (!info.Exists) throw new RetentionError("Missing model file");
            return $"{file.Rfilename}:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{info.CreationTimeUtc.Ticks}";
        }).ToArray();
}

public static class ModelFiles
{
    /// <summary>Checks every pinned file's size and checksum; throws <see cref="RetentionError"/> with a content-free message on the first problem.</summary>
    public static void VerifyAppModels(string directory, ModelManifest manifest)
    {
        var hasher = new StreamingFileHasher();
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(directory, file.Rfilename.Replace('/', Path.DirectorySeparatorChar));
            if (Digests.FileSize(path) is not { } size) throw new RetentionError("Missing model file");
            if (size != file.Size) throw new RetentionError("Model file size mismatch");
            string digest;
            try { digest = hasher.HexDigest(path, file.Algorithm, () => false); }
            catch (ModelDownloadException) { throw new RetentionError("Missing model file"); }
            if (digest != file.Checksum) throw new RetentionError("Model checksum mismatch");
        }
    }
}
