// Port of app/HistoryRouter.swift.
namespace Murmur.Core;

// Routing history between disk and memory (history off, session-only entries) and its summary text.

/// <summary>The disk side of history, as delegates over <see cref="HistoryStore"/>.</summary>
public sealed record HistoryDisk<TEntry>(Action<TEntry> Save, Func<IReadOnlyList<TEntry>> Entries, Action<TEntry> Delete);

/// <summary>
/// Every history write goes through here. With history on, entries go to disk. With history off, they stay in memory for this app session only,
/// so a held cleanup or a failed insertion can still be copied from History until Murmur quits; nothing is written to disk. Thread-safe: the first
/// save of a dictation happens off the UI thread.
/// </summary>
public sealed class HistoryRouter<TEntry> where TEntry : IRetainedHistoryItem
{
    /// <summary>Memory entries above this count are dropped oldest first.</summary>
    public int MemoryLimit { get; }
    readonly HistoryDisk<TEntry> disk;
    readonly Lock gate = new();
    readonly Dictionary<string, TEntry> memory = [];
    bool persists;

    public HistoryRouter(HistoryDisk<TEntry> disk, bool persists, int memoryLimit = 100)
    {
        this.disk = disk; this.persists = persists; MemoryLimit = Math.Max(1, memoryLimit);
    }

    /// <summary>Changing this does not move existing entries: memory entries stay in memory, disk entries stay on disk.</summary>
    public bool PersistsTranscripts { get { lock (gate) return persists; } set { lock (gate) persists = value; } }

    /// <summary>An entry already held in memory stays in memory, so one dictation never splits across both.</summary>
    public void Save(TEntry entry)
    {
        lock (gate)
        {
            if (!persists || memory.ContainsKey(entry.Id))
            {
                memory[entry.Id] = entry;
                if (memory.Count > MemoryLimit)
                {
                    foreach (var old in memory.Values.OrderBy(e => e.RecordedAt).Take(memory.Count - MemoryLimit).ToList()) memory.Remove(old.Id);
                }
                return;
            }
        }
        disk.Save(entry);
    }

    /// <summary>Replaces an existing entry where it already lives (memory or disk), whatever <see cref="PersistsTranscripts"/> says now.</summary>
    public void Update(TEntry entry)
    {
        lock (gate)
        {
            if (memory.ContainsKey(entry.Id)) { memory[entry.Id] = entry; return; }
        }
        disk.Save(entry);
    }

    /// <summary>Disk and memory entries, newest first. A memory entry wins over a disk entry with the same id.</summary>
    public List<TEntry> Entries()
    {
        var stored = disk.Entries();
        Dictionary<string, TEntry> held;
        lock (gate) held = new(memory);
        return stored.Where(e => !held.ContainsKey(e.Id)).Concat(held.Values).OrderByDescending(e => e.RecordedAt).ToList();
    }

    public void Delete(TEntry entry)
    {
        bool wasHeld;
        lock (gate) wasHeld = memory.Remove(entry.Id);
        if (!wasHeld) disk.Delete(entry);
    }

    public IReadOnlySet<string> SessionOnlyIDs { get { lock (gate) return memory.Keys.ToHashSet(); } }

    /// <summary>Applies time-based retention to memory entries (disk entries use <see cref="HistoryStore.Purge"/>).</summary>
    public void PurgeSessionOnly(DateTime recordedAtOrBefore)
    {
        lock (gate) foreach (var id in memory.Where(e => e.Value.RecordedAt <= recordedAtOrBefore).Select(e => e.Key).ToList()) memory.Remove(id);
    }

    public void ClearSessionOnly() { lock (gate) memory.Clear(); }
}

public static class HistoryText
{
    public static string Summary(HistoryMode mode, int count, int sessionOnly)
    {
        var noun = count == 1 ? "transcript" : "transcripts";
        return mode switch
        {
            HistoryMode.On => sessionOnly == 0 ? $"Saved locally · {count} {noun}" : $"Saved locally · {count} {noun}, {sessionOnly} kept only until Murmur quits",
            _ => $"History is off · {count} {noun} · new transcripts are kept only until Murmur quits",
        };
    }

    public const string SessionOnlyNote = "Not saved to disk. This text is lost when Murmur quits.";

    /// <summary>The first sentence of a completion message.</summary>
    public static string Saved(bool persists) => persists ? "Saved in History." : "Kept in History until Murmur quits.";
}
