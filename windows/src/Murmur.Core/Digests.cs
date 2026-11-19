// Port of app/Digests.swift.
using System.Security.Cryptography;
using System.Text;

namespace Murmur.Core;

// shortcut: the Swift file hand-rolls SHA-1/SHA-256 so Linux checks build without CryptoKit; .NET ships both, so this port uses
// System.Security.Cryptography. The golden file pins the digests the Swift code produces.

public enum ModelHashAlgorithm
{
    /// <summary>Plain SHA-256 of the file bytes (Hugging Face LFS <c>sha256</c>).</summary>
    Sha256,
    /// <summary>SHA-1 of <c>"blob &lt;size&gt;\0" + bytes</c> (the <c>git_blob_sha1</c> field in the pins). An integrity check against pinned values, not a security boundary on its own.</summary>
    BlobSha1,
}

public static class Digests
{
    public static string HexString(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    public static string Sha256Hex(ReadOnlySpan<byte> data) => HexString(SHA256.HashData(data));
    public static string Sha1Hex(ReadOnlySpan<byte> data) => HexString(SHA1.HashData(data));
    public static string BlobSha1Hex(ReadOnlySpan<byte> data) => Sha1Hex([.. Encoding.UTF8.GetBytes($"blob {data.Length}\0"), .. data]);

    /// <summary>The size of a regular file; null for a missing path, a directory or a link.</summary>
    public static long? FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0 ? info.Length : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>Computes file digests. Injected so tests can count calls or simulate failures.</summary>
public interface IFileHasher
{
    /// <summary>Returns the lowercase hex digest. Reads in bounded chunks and throws <see cref="ModelDownloadError.Cancelled"/> promptly once <paramref name="isCancelled"/> returns true.</summary>
    string HexDigest(string file, ModelHashAlgorithm algorithm, Func<bool> isCancelled);
}

/// <summary>Default hasher: streams the file in 1 MiB chunks.</summary>
public sealed class StreamingFileHasher(int chunkSize = 1 << 20) : IFileHasher
{
    public string HexDigest(string file, ModelHashAlgorithm algorithm, Func<bool> isCancelled)
    {
        FileStream stream;
        try { stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ModelDownloadException(ModelDownloadError.FileSystem); }
        using (stream)
        using (var hash = IncrementalHash.CreateHash(algorithm == ModelHashAlgorithm.Sha256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1))
        {
            if (algorithm == ModelHashAlgorithm.BlobSha1)
            {
                var size = Digests.FileSize(file) ?? throw new ModelDownloadException(ModelDownloadError.FileSystem);
                hash.AppendData(Encoding.UTF8.GetBytes($"blob {size}\0"));
            }
            var buffer = new byte[chunkSize];
            while (true)
            {
                if (isCancelled()) throw new ModelDownloadException(ModelDownloadError.Cancelled);
                int read;
                try { read = stream.Read(buffer, 0, buffer.Length); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ModelDownloadException(ModelDownloadError.FileSystem); }
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
            return Digests.HexString(hash.GetHashAndReset());
        }
    }
}
