// Ports the hash checks of app/check_model_download.swift (hashVectors). The digests themselves come from System.Security.Cryptography; DigestsTests pins the
// published vectors and the file hasher's streaming, cancellation and error behavior. The Swift outputs are compared in ModelDownloadGoldenTests.
using System.Text;
using Murmur.Core;
using static Murmur.Core.Tests.ModelTestSupport;

namespace Murmur.Core.Tests;

public class DigestsTests
{
    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void PublishedVectors()
    {
        // FIPS 180 vectors and Python hashlib outputs.
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Digests.Sha256Hex([]));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Digests.Sha256Hex(Utf8("abc")));
        Assert.Equal("da39a3ee5e6b4b0d3255bfef95601890afd80709", Digests.Sha1Hex([]));
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", Digests.Sha1Hex(Utf8("abc")));
        var twoBlock = Utf8("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq");
        Assert.Equal("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1", Digests.Sha256Hex(twoBlock));
        Assert.Equal("84983e441c3bd26ebaae4aa1f95129e5e54670f1", Digests.Sha1Hex(twoBlock));
        Assert.Equal("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0", Digests.Sha256Hex(Enumerable.Repeat((byte)0x61, 1_000_000).ToArray()));
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", Digests.BlobSha1Hex(Utf8("hello\n")));
        var mebibyte = Pattern(1 << 20);
        Assert.Equal("06b7bbfb7824aa03382051691630eb26de85102d1b08a81e907ec0744cd8a286", Digests.Sha256Hex(mebibyte));
        Assert.Equal("95421610b8ddd86c86e3269bfd24d2a79199245f", Digests.Sha1Hex(mebibyte));
        Assert.Equal("0123456789abcdef", Digests.HexString([0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef]));
    }

    [Fact]
    public void FileHasherStreamsFromDisk()
    {
        using var temp = new TempDir();
        var file = temp.Combine("vector.bin");
        var mebibyte = Pattern(1 << 20);
        File.WriteAllBytes(file, mebibyte);
        foreach (var chunk in new[] { 1000, 70_001, 1 << 20, 4 << 20 })
        {
            var hasher = new StreamingFileHasher(chunk);
            Assert.Equal("06b7bbfb7824aa03382051691630eb26de85102d1b08a81e907ec0744cd8a286", hasher.HexDigest(file, ModelHashAlgorithm.Sha256, () => false));
            Assert.Equal(Digests.BlobSha1Hex(mebibyte), hasher.HexDigest(file, ModelHashAlgorithm.BlobSha1, () => false));
        }
        var empty = temp.Combine("empty.bin");
        File.WriteAllBytes(empty, []);
        Assert.Equal(Digests.BlobSha1Hex([]), new StreamingFileHasher().HexDigest(empty, ModelHashAlgorithm.BlobSha1, () => false));
    }

    [Fact]
    public void FileHasherHonorsCancelAndReportsMissingFiles()
    {
        using var temp = new TempDir();
        var file = temp.Combine("vector.bin");
        File.WriteAllBytes(file, Pattern(5000));
        var hasher = new StreamingFileHasher(100);
        Assert.Equal(ModelDownloadError.Cancelled, Assert.Throws<ModelDownloadException>(() => hasher.HexDigest(file, ModelHashAlgorithm.Sha256, () => true)).Error);
        var reads = 0;
        Assert.Equal(ModelDownloadError.Cancelled, Assert.Throws<ModelDownloadException>(() => hasher.HexDigest(file, ModelHashAlgorithm.Sha256, () => ++reads > 3)).Error);
        Assert.Equal(4, reads);
        Assert.Equal(ModelDownloadError.FileSystem, Assert.Throws<ModelDownloadException>(() => hasher.HexDigest(temp.Combine("none"), ModelHashAlgorithm.Sha256, () => false)).Error);
        Assert.Equal(ModelDownloadError.FileSystem, Assert.Throws<ModelDownloadException>(() => hasher.HexDigest(temp.Path, ModelHashAlgorithm.Sha256, () => false)).Error);
    }

    [Fact]
    public void FileSizeIsForRegularFilesOnly()
    {
        using var temp = new TempDir();
        File.WriteAllBytes(temp.Combine("a"), new byte[12]);
        Assert.Equal(12, Digests.FileSize(temp.Combine("a")));
        Assert.Null(Digests.FileSize(temp.Combine("missing")));
        Assert.Null(Digests.FileSize(temp.Path));
        try
        {
            File.CreateSymbolicLink(temp.Combine("link"), temp.Combine("a"));
            Assert.Null(Digests.FileSize(temp.Combine("link")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Windows without the symlink privilege: nothing to check */ }
    }
}
