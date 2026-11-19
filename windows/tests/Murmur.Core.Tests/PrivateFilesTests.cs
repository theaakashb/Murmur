// Tests for PrivateFiles.cs (owner-only private files with atomic replacement) and shared helpers for the store tests.
using System.Runtime.CompilerServices;
using System.Text;
using Murmur.Core;

namespace Murmur.Core.Tests;

/// <summary>Collects every failed check with its label, as the Swift check scripts do, then fails once at <see cref="Done"/>.</summary>
internal sealed class Checker
{
    readonly List<string> failures = [];
    public void Check(bool condition, string label, [CallerLineNumber] int line = 0)
    {
        if (!condition) failures.Add($"[line {line}] {label}");
    }
    public void Done() => Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
}

/// <summary>A fresh temp folder per test run, removed when the process exits.</summary>
internal static class TempDirs
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "murmur-core-tests-" + Guid.NewGuid().ToString("N"));
    static TempDirs() => AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } };

    public static string New(string name = "d")
    {
        var path = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N"));
        return path;
    }
}

public class PrivateFilesTests
{
    [Fact]
    public void PrepareDirectoryCreatesOwnerOnlyFolder()
    {
        var dir = Path.Combine(TempDirs.New(), "nested", "store");
        PrivateFiles.PrepareDirectory(dir);
        Assert.True(Directory.Exists(dir));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        PrivateFiles.PrepareDirectory(dir); // idempotent
    }

    [Fact]
    public void WriteReplacesAtomicallyAndReadsBack()
    {
        var dir = TempDirs.New();
        PrivateFiles.PrepareDirectory(dir);
        var file = Path.Combine(dir, "x.json");
        Assert.Null(PrivateFiles.Read(file));
        PrivateFiles.Write(Encoding.UTF8.GetBytes("one"), file);
        PrivateFiles.Write(Encoding.UTF8.GetBytes("two"), file);
        Assert.Equal("two", Encoding.UTF8.GetString(PrivateFiles.Read(file)!));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.Equal(["x.json"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName)); // no temporary file is left behind
    }

    [Fact]
    public void RefusesNonRegularTargets()
    {
        var dir = TempDirs.New();
        PrivateFiles.PrepareDirectory(dir);
        var asDirectory = Path.Combine(dir, "folder");
        Directory.CreateDirectory(asDirectory);
        Assert.Throws<StoreError>(() => PrivateFiles.Write([1], asDirectory));
        Assert.Throws<StoreError>(() => PrivateFiles.Read(asDirectory));
        var asFile = Path.Combine(dir, "plain");
        File.WriteAllText(asFile, "x");
        Assert.Throws<StoreError>(() => PrivateFiles.PrepareDirectory(asFile));
        if (OperatingSystem.IsWindows()) return;
        var link = Path.Combine(dir, "link");
        File.CreateSymbolicLink(link, asFile);
        Assert.Throws<StoreError>(() => PrivateFiles.Write([1], link));
        Assert.Throws<StoreError>(() => PrivateFiles.Read(link));
        var folderLink = Path.Combine(dir, "folderLink");
        Directory.CreateSymbolicLink(folderLink, asDirectory);
        Assert.Throws<StoreError>(() => PrivateFiles.PrepareDirectory(folderLink));
    }

    [Fact]
    public void FailedWriteLeavesTheOldFileAndNoTemporaryFile()
    {
        if (OperatingSystem.IsWindows()) return; // read-only folders are a Unix mode check
        var dir = TempDirs.New();
        PrivateFiles.PrepareDirectory(dir);
        var file = Path.Combine(dir, "x.json");
        PrivateFiles.Write([1, 2, 3], file);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // Root and some file systems ignore the mode: then the write cannot fail and there is nothing to check.
            bool writable;
            try { File.WriteAllBytes(Path.Combine(dir, ".probe"), []); File.Delete(Path.Combine(dir, ".probe")); writable = true; } catch (UnauthorizedAccessException) { writable = false; }
            if (writable) return;
            Assert.Throws<UnauthorizedAccessException>(() => PrivateFiles.Write([9], file));
        }
        finally { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Assert.Equal([1, 2, 3], PrivateFiles.Read(file));
        Assert.Equal(["x.json"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));
    }
}
