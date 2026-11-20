// Port of app/MeetingStore.swift.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Murmur.Core;

public sealed class MeetingStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Directory { get; }
    public string Unreadable => Path.Combine(Directory, "unreadable");

    public MeetingStore(string directory)
    {
        if (FileLinks.IsLink(directory)) throw new RetentionError("Meetings require a real directory");
        PrivateFiles.PrepareDirectory(directory);
        Directory = directory;
    }

    string FilePath(string id) => Guid.TryParseExact(id, "D", out _) ? Path.Combine(Directory, id + ".json")
        : throw new RetentionError("Invalid meeting identifier");

    public void Save(Meeting meeting)
    {
        var path = FilePath(meeting.Id);
        if (FileLinks.IsLink(path)) throw new RetentionError("Meeting file is not a regular file");
        PrivateFiles.Write(JsonSerializer.SerializeToUtf8Bytes(meeting, Json), path);
    }

    public IReadOnlyList<Meeting> Meetings()
    {
        var found = new List<Meeting>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory).Where(path => Path.GetExtension(path) == ".json"))
        {
            if (FileLinks.IsLink(path)) continue;
            Meeting? meeting = null;
            try { meeting = JsonSerializer.Deserialize<Meeting>(File.ReadAllBytes(path), Json); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
            if (meeting is { Id: { } id, Source: not null, Lines: not null, Gaps: not null, MyNotes: not null } &&
                Guid.TryParseExact(id, "D", out _) && Path.GetFileName(path) == id + ".json")
            {
                found.Add(meeting);
                continue;
            }

            if (FileLinks.IsLink(Unreadable)) throw new RetentionError("Unreadable meetings require a real directory");
            PrivateFiles.PrepareDirectory(Unreadable);
            File.Move(path, Path.Combine(Unreadable, Guid.NewGuid().ToString("D") + ".json"));
        }
        return found.OrderByDescending(meeting => meeting.RecordedAt).ToArray();
    }

    public bool Exists(string id) => Guid.TryParseExact(id, "D", out _) && File.Exists(FilePath(id)) && !FileLinks.IsLink(FilePath(id));
    public void Delete(string id) => File.Delete(FilePath(id));
    public long Size(string id) => Exists(id) ? new FileInfo(FilePath(id)).Length : 0;

    public IReadOnlyList<string> Purge(DateTime? cutoff)
    {
        if (cutoff is null) return [];
        var expired = Meetings().Where(meeting => meeting.RecordedAt <= cutoff).Select(meeting => meeting.Id).ToArray();
        foreach (var id in expired) Delete(id);
        PurgeUnreadable(cutoff.Value);
        return expired;
    }

    public void PurgeUnreadable(DateTime cutoff)
    {
        if (FileLinks.IsLink(Unreadable) || !System.IO.Directory.Exists(Unreadable)) return;
        foreach (var path in System.IO.Directory.EnumerateFiles(Unreadable))
            if (!FileLinks.IsLink(path) && File.GetLastWriteTimeUtc(path) <= SwiftDate.Normalize(cutoff)) File.Delete(path);
    }
}

public sealed record CappedHistoryItem(string Id, DateTime RecordedAt, bool IsMeeting) : IRetainedHistoryItem;

public static class MeetingCap
{
    public static HistoryCapResult<CappedHistoryItem> Enforce(IEnumerable<IRetainedHistoryItem> entries, IEnumerable<Meeting> meetings,
        string historyDirectory, MeetingStore store, HistoryRetention retention, Action<string> deleteEntry, long capBytes = HistoryCap.DefaultCapBytes)
    {
        var sizes = new Dictionary<string, long>();
        var items = new List<CappedHistoryItem>();
        foreach (var entry in entries)
        {
            var path = Path.Combine(historyDirectory, entry.Id + ".json");
            sizes[entry.Id] = Guid.TryParseExact(entry.Id, "D", out _) && File.Exists(path) && !FileLinks.IsLink(path) ? new FileInfo(path).Length : 0;
            items.Add(new(entry.Id, entry.RecordedAt, false));
        }
        foreach (var meeting in meetings)
        {
            sizes[meeting.Id] = store.Size(meeting.Id);
            items.Add(new(meeting.Id, meeting.RecordedAt, true));
        }
        var result = HistoryCap.Plan(items, sizes, Math.Max(0, HistoryCap.Usage(historyDirectory) - sizes.Values.Sum()), retention, capBytes);
        foreach (var item in result.Dropped)
        {
            if (item.IsMeeting) store.Delete(item.Id);
            else deleteEntry(item.Id);
        }
        return result;
    }
}

public static class MeetingAudio
{
    public static string Folder(string root, string id) => Guid.TryParseExact(id, "D", out _) ? Path.Combine(root, id)
        : throw new RetentionError("Invalid meeting identifier");

    public static string Name(MeetingStream stream, double start) =>
        FormattableString.Invariant($"{(stream == MeetingStream.Me ? "me" : "them")}-{checked((long)Math.Round(start * 1000, MidpointRounding.AwayFromZero))}.wav");

    public static (MeetingStream Stream, double Start)? Parse(string path)
    {
        if (Path.GetExtension(path) != ".wav") return null;
        var parts = Path.GetFileNameWithoutExtension(path).Split('-');
        if (parts.Length != 2 || parts[0] is not ("me" or "them") ||
            !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long ms) || ms < 0) return null;
        return (parts[0] == "me" ? MeetingStream.Me : MeetingStream.Them, ms / 1000.0);
    }

    public static IReadOnlyList<string> Leftovers(string root, string id)
    {
        var folder = Folder(root, id);
        if (FileLinks.IsLink(root) || FileLinks.IsLink(folder) || !Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder).Where(path => !FileLinks.IsLink(path) && Parse(path) is not null)
            .OrderBy(path => Parse(path)!.Value.Start).ThenBy(path => path, StringComparer.Ordinal).ToArray();
    }

    public static int Purge(string root, IReadOnlySet<string> keep, IReadOnlySet<string>? active = null, DateTime? now = null)
    {
        if (FileLinks.IsLink(root) || !Directory.Exists(root)) return 0;
        int failed = 0;
        var current = SwiftDate.Normalize(now ?? DateTime.UtcNow);
        void Remove(string path)
        {
            try { FileLinks.DeleteTree(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
        }
        foreach (var folder in Directory.EnumerateFileSystemEntries(root))
        {
            var id = Path.GetFileName(folder);
            if (active?.Contains(id) == true) continue;
            if (!keep.Contains(id) || FileLinks.IsLink(folder) || !Directory.Exists(folder)) { Remove(folder); continue; }
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                var created = File.GetCreationTimeUtc(path);
                if (FileLinks.IsLink(path) || !File.Exists(path) || Parse(path) is null || created > current ||
                    (current - created).TotalSeconds >= RetryWindow.LifetimeSeconds) Remove(path);
            }
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Remove(folder);
        }
        return failed;
    }
}
