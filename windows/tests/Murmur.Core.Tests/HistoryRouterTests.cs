// Ports checkHistoryRouter from app/check_app_services.swift.
using Murmur.Core;

namespace Murmur.Core.Tests;

public class HistoryRouterTests
{
    sealed record Item(string Id, DateTime RecordedAt, string Text) : IRetainedHistoryItem;

    sealed class FakeDisk
    {
        public readonly Dictionary<string, Item> Items = [];
        public bool FailSave;
        public HistoryDisk<Item> Disk => new(
            item => { if (FailSave) throw new RetentionError("disk full"); Items[item.Id] = item; },
            () => Items.Values.ToList(),
            item => { if (!Items.Remove(item.Id)) throw new RetentionError("missing"); });
    }

    static Item MakeItem(double secondsAgo, string text = "t", DateTime? now = null) =>
        new(Guid.NewGuid().ToString().ToUpperInvariant(), (now ?? DateTime.UtcNow).AddSeconds(-secondsAgo), text);

    [Fact]
    public void RoutesBetweenDiskAndMemory()
    {
        var c = new Checker();
        var fake = new FakeDisk();
        var router = new HistoryRouter<Item>(fake.Disk, persists: true);
        var a = MakeItem(60);
        router.Save(a);
        c.Check(fake.Items[a.Id] == a && router.SessionOnlyIDs.Count == 0, "history on writes to disk");

        router.PersistsTranscripts = false;
        var held = MakeItem(10, "held cleanup");
        router.Save(held);
        c.Check(!fake.Items.ContainsKey(held.Id) && router.SessionOnlyIDs.SetEquals([held.Id]), "history off keeps the entry in memory only");
        held = held with { Text = "insertion failed" };
        router.PersistsTranscripts = true;
        router.Save(held);
        c.Check(!fake.Items.ContainsKey(held.Id) && router.SessionOnlyIDs.SetEquals([held.Id]), "a memory entry stays in memory after history is turned back on");
        var listed = router.Entries();
        c.Check(listed.Select(e => e.Id).SequenceEqual([held.Id, a.Id]) && listed[0].Text == "insertion failed", "entries merge memory and disk, newest first, latest status");

        // A disk failure is reported, not silently moved to memory.
        fake.FailSave = true;
        Assert.Throws<RetentionError>(() => router.Save(MakeItem(1)));
        fake.FailSave = false;

        router.Delete(held);
        c.Check(router.SessionOnlyIDs.Count == 0 && fake.Items.ContainsKey(a.Id), "deleting a memory entry leaves disk alone");
        router.Delete(a);
        c.Check(fake.Items.Count == 0, "deleting a disk entry removes it from disk");

        // Same id on disk and in memory: memory wins in the list, delete removes the memory copy first.
        var dual = MakeItem(5);
        fake.Items[dual.Id] = dual;
        router.PersistsTranscripts = false;
        router.Save(dual with { Text = "newer" });
        c.Check(router.Entries().Where(e => e.Id == dual.Id).Select(e => e.Text).SequenceEqual(["newer"]), "memory copy wins over disk copy");
        // Update goes where the entry lives.
        router.PersistsTranscripts = true;
        router.Update(dual with { Text = "updated in memory" });
        c.Check(fake.Items[dual.Id].Text == "t" && router.Entries().Single(e => e.Id == dual.Id).Text == "updated in memory", "update replaces a memory entry in memory");
        var onDisk = MakeItem(4);
        fake.Items[onDisk.Id] = onDisk;
        router.Update(onDisk with { Text = "updated on disk" });
        c.Check(fake.Items[onDisk.Id].Text == "updated on disk" && !router.SessionOnlyIDs.Contains(onDisk.Id), "update replaces a disk entry on disk");

        // Memory bound and retention.
        var small = new HistoryRouter<Item>(new FakeDisk().Disk, persists: false, memoryLimit: 3);
        var now = DateTime.UtcNow;
        var olds = Enumerable.Range(0, 5).Select(i => MakeItem(50 - i * 10, $"n{i}", now)).ToList();
        foreach (var entry in olds) small.Save(entry);
        c.Check(small.SessionOnlyIDs.SetEquals(olds.TakeLast(3).Select(e => e.Id)), "memory keeps the newest entries within its limit");
        small.PurgeSessionOnly(now.AddSeconds(-25));
        c.Check(small.SessionOnlyIDs.SetEquals(olds.TakeLast(2).Select(e => e.Id)), "memory retention drops entries at or before the cutoff");
        small.ClearSessionOnly();
        c.Check(small.Entries().Count == 0, "clear empties memory");

        // Concurrent saves from background threads while another lists.
        var concurrent = new HistoryRouter<Item>(new FakeDisk().Disk, persists: false, memoryLimit: 1000);
        Parallel.For(0, 200, i => { if (i % 2 == 0) concurrent.Save(MakeItem(i)); else concurrent.Entries(); });
        c.Check(concurrent.SessionOnlyIDs.Count == 100, "concurrent saves are all kept");
        c.Done();
    }
}
