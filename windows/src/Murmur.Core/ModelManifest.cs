// Port of app/ModelManifest.swift.
using System.Text;
using System.Text.Json;

namespace Murmur.Core;

public sealed record ModelManifestFile(string Rfilename, long Size, string Checksum, ModelHashAlgorithm Algorithm);

/// <summary>Where the declaration comes from, e.g. "Upstream NVIDIA model card".</summary>
public sealed record ModelLicenseDeclaration(string Source, string Name, Uri Url)
{
    public string Id => Name + "|" + Url.AbsoluteUri;
}

/// <summary>License facts to show before download. <see cref="IsPinned"/> is true only when the manifest itself records a license; otherwise the declarations are what the model cards say, and <see cref="Note"/> says so plainly.</summary>
public sealed record ModelLicenseInfo(bool IsPinned, IReadOnlyList<ModelLicenseDeclaration> Declarations, string Note)
{
    public bool Equals(ModelLicenseInfo? other) => other is not null && IsPinned == other.IsPinned && Note == other.Note && Declarations.SequenceEqual(other.Declarations);
    public override int GetHashCode() => HashCode.Combine(IsPinned, Note, Declarations.Count);
}

public sealed class ModelManifest
{
    /// <summary>The pins' list of files a model needs; live words use another list.</summary>
    public const string BatchList = "batch_files";

    public string Repository { get; }
    /// <summary>Immutable 40-hex commit; branch or tag names are rejected so the source cannot drift.</summary>
    public string Revision { get; }
    public IReadOnlyList<ModelManifestFile> Files { get; }
    public ModelLicenseInfo License { get; }
    /// <summary>True for a model whose files exist locally but are not uploaded yet (<c>"staged": true</c> in windows/shared/windows-models.json): it cannot be downloaded.</summary>
    public bool Staged { get; }

    /// <summary>Sum of pinned file sizes: the download size to show before downloading.</summary>
    public long TotalBytes => Files.Sum(f => f.Size);

    public static ModelManifest Load(string path, string repository, string list = BatchList, ModelLicenseInfo? unpinnedLicense = null)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ModelDownloadException(ModelDownloadError.InvalidManifest); }
        return new ModelManifest(data, repository, list, unpinnedLicense);
    }

    public ModelManifest(string json, string repository, string list = BatchList, ModelLicenseInfo? unpinnedLicense = null)
        : this(Encoding.UTF8.GetBytes(json), repository, list, unpinnedLicense) { }

    /// <summary>Parses artifact-pins.json. Throws <see cref="ModelDownloadError.UnsafePath"/> for traversal-like names, otherwise <see cref="ModelDownloadError.InvalidManifest"/>.
    /// <paramref name="unpinnedLicense"/> is what the model cards declare when the pins record no license.</summary>
    public ModelManifest(byte[] data, string repository, string list = BatchList, ModelLicenseInfo? unpinnedLicense = null)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(data); } catch (JsonException) { throw Invalid(); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object
                || !models.TryGetProperty(repository, out var model) || model.ValueKind != JsonValueKind.Object
                || !model.TryGetProperty("revision", out var rev) || rev.ValueKind != JsonValueKind.String
                || !model.TryGetProperty("files", out var entries) || entries.ValueKind != JsonValueKind.Array
                || !model.TryGetProperty(list, out var batch) || batch.ValueKind != JsonValueKind.Array || batch.GetArrayLength() == 0
                || !IsSafeRepository(repository) || !IsHex(rev.GetString()!, 40)) throw Invalid();
            var byName = new Dictionary<string, JsonElement>();
            foreach (var entry in entries.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("rfilename", out var n) && n.ValueKind == JsonValueKind.String) byName[n.GetString()!] = entry;
            var files = new List<ModelManifestFile>();
            var seen = new HashSet<string>();
            foreach (var item in batch.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw Invalid();
                var name = item.GetString()!;
                if (!IsSafeRelativePath(name)) throw new ModelDownloadException(ModelDownloadError.UnsafePath);
                if (!seen.Add(name) || !byName.TryGetValue(name, out var entry)
                    || !entry.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out var size) || size < 0) throw Invalid();
                if (entry.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
                {
                    if (!lfs.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String || !IsHex(sha.GetString()!, 64)) throw Invalid();
                    if (lfs.TryGetProperty("size", out var lfsSize) && lfsSize.TryGetInt64(out var lfsBytes) && lfsBytes != size) throw Invalid();
                    files.Add(new ModelManifestFile(name, size, sha.GetString()!, ModelHashAlgorithm.Sha256));
                }
                else if (entry.TryGetProperty("git_blob_sha1", out var sha1) && sha1.ValueKind == JsonValueKind.String && IsHex(sha1.GetString()!, 40))
                    files.Add(new ModelManifestFile(name, size, sha1.GetString()!, ModelHashAlgorithm.BlobSha1));
                else throw Invalid();
            }
            // Reject names where one file would sit "inside" another file, e.g. "a" and "a/b".
            foreach (var file in files)
                if (files.Any(other => other.Rfilename.StartsWith(file.Rfilename + "/", StringComparison.Ordinal))) throw new ModelDownloadException(ModelDownloadError.UnsafePath);
            Repository = repository;
            Revision = rev.GetString()!;
            Files = files;
            Staged = model.TryGetProperty("staged", out var staged) && staged.ValueKind == JsonValueKind.True;
            if (model.TryGetProperty("license", out var license) && license.ValueKind == JsonValueKind.Object
                && license.TryGetProperty("name", out var licenseName) && licenseName.ValueKind == JsonValueKind.String && licenseName.GetString()!.Length > 0
                && license.TryGetProperty("url", out var licenseUrl) && licenseUrl.ValueKind == JsonValueKind.String
                && Uri.TryCreate(licenseUrl.GetString(), UriKind.Absolute, out var url) && url.Scheme == "https")
                License = new ModelLicenseInfo(true, [new ModelLicenseDeclaration("Pinned model manifest", licenseName.GetString()!, url)], "This license is recorded in Murmur's pinned model manifest.");
            else if (unpinnedLicense is not null) License = unpinnedLicense;
            else License = new ModelLicenseInfo(false, [], "The license for this model is not recorded in Murmur's pinned model manifest.");
        }
    }

    static ModelDownloadException Invalid() => new(ModelDownloadError.InvalidManifest);

    /// <summary>Relative, forward-slash path with no empty, ".", or ".." components, no backslashes, control characters, or leading "~". Anything else could escape the model directory.
    /// Windows adds its own limits (the Swift version has none of these): no ':' (alternate data streams, drive letters), no &lt;&gt;"|?*, no trailing '.' or space, no device names such as NUL.</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (path.Length == 0 || Encoding.UTF8.GetByteCount(path) > 1024 || path.StartsWith('/') || path.StartsWith('~') || path.Contains('\\')) return false;
        if (path.Any(c => c < 0x20 || c == 0x7f)) return false;
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".part", StringComparison.Ordinal)) return false;
            if (part.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) >= 0 || part.EndsWith('.') || part.EndsWith(' ') || IsDeviceName(part)) return false;
        }
        return true;
    }

    static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" };
    static bool IsDeviceName(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ');
        return Devices.Contains(stem) || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');
    }

    public static bool IsSafeRepository(string repository)
    {
        var parts = repository.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p != "." && p != ".." && p.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.'));
    }

    public static bool IsHex(string value, int length) => value.Length == length && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary><c>https://huggingface.co/&lt;repo&gt;/resolve/&lt;revision&gt;/&lt;rfilename&gt;</c>, each path component percent-encoded.</summary>
    public Uri? ResolveUrl(ModelManifestFile file, string baseUrl = "https://huggingface.co")
    {
        var encoded = string.Join('/', file.Rfilename.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(PercentEncode));
        var path = $"{Repository}/resolve/{Revision}/{encoded}";
        return Uri.TryCreate(baseUrl.EndsWith('/') ? baseUrl + path : baseUrl + "/" + path, UriKind.Absolute, out var url) ? url : null;
    }

    /// <summary>Foundation's <c>urlPathAllowed</c> without "/?#;".</summary>
    static string PercentEncode(string component)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(component))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || "-._~!$&'()*+,=:@".Contains(c))) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
