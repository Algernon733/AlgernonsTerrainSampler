using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using AlgernonsTerrainSampler.Tiles;

namespace AlgernonsTerrainSampler.Tests;

public class TileDiskCacheTests
{
    private static readonly string Current = TerrainTileIdentity.Hash(writer => writer.Write("current"));
    private static readonly string Old = TerrainTileIdentity.Hash(writer => writer.Write("old"));

    private static TerrainTileDiskCache Open(string root, string identity, int megabytes, Action<string>? log = null, string? sharedParent = null, int unusedDays = 0)
    {
        TerrainTileDiskCache cache = new(root, identity, megabytes, log, sharedParent, unusedDays);
        Wait.Until(() => cache.Ready, () => { }, "the disk index");
        return cache;
    }

    private static byte[] Blob(int kibibytes) => new byte[kibibytes * 1024];

    // A file left in a folder by some earlier cache, last used when given.
    private static string Plant(string root, string folder, string name, int kibibytes, DateTime? used = null)
    {
        string path = Path.Combine(root, folder, name);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Blob(kibibytes));
        if (used != null)
            File.SetLastWriteTimeUtc(path, used.Value);

        return path;
    }

    // Bytes that pass the cache's checks: a sound header for the key, and a payload of random bytes to the size asked.
    private static byte[] Tile(TerrainTileKey key, int kibibytes)
    {
        byte[] data = new byte[kibibytes * 1024];
        Random.Shared.NextBytes(data.AsSpan(TerrainTileCodec.HeaderBytes));
        Span<byte> header = data.AsSpan(0, TerrainTileCodec.HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header, TerrainTileCodec.Magic);
        header[4] = TerrainTileCodec.FormatVersion;
        header[5] = (byte)key.Step;
        BinaryPrimitives.WriteInt32LittleEndian(header[6..], key.TileX);
        BinaryPrimitives.WriteInt32LittleEndian(header[10..], key.TileZ);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], TerrainTileKey.Cells);
        BinaryPrimitives.WriteUInt16LittleEndian(header[16..], TerrainTileCodec.AllFields);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], data.Length - TerrainTileCodec.HeaderBytes);
        _ = SHA256.HashData(data.AsSpan(TerrainTileCodec.HeaderBytes), header[22..]);
        Assert.True(TerrainTileCodec.Verify(data, key));
        return data;
    }

    private static byte[]? Read(TerrainTileDiskCache cache, TerrainTileKey key)
    {
        byte[]? read = null;
        bool done = false;
        Assert.True(cache.TryRead(key, data => { read = data; done = true; }));
        Wait.Until(() => done, () => { }, $"the read of {key}");
        return read;
    }

    // The cache's own folder replaced by a file of the same name, so every write into it fails, as on a lost drive.
    private static void Break(TerrainTileDiskCache cache)
    {
        Directory.Delete(cache.Folder, recursive: true);
        File.WriteAllText(cache.Folder, "");
    }

    private static void Mend(TerrainTileDiskCache cache)
    {
        File.Delete(cache.Folder);
        _ = Directory.CreateDirectory(cache.Folder);
    }

    [Fact]
    public void The_least_recently_used_tile_goes_when_the_quota_is_full()
    {
        using TemporaryFolder folder = new();
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 1);
        cache.Store(new TerrainTileKey(4, 1, 1), Blob(400));
        cache.Store(new TerrainTileKey(4, 2, 2), Blob(400));
        cache.Store(new TerrainTileKey(4, 3, 3), Blob(400));

        Assert.True(cache.Bytes <= 1024 * 1024);
        Assert.False(cache.Contains(new TerrainTileKey(4, 1, 1)));
        Assert.True(cache.Contains(new TerrainTileKey(4, 3, 3)));
    }

    [Fact]
    public void A_read_tile_outlasts_one_written_after_it()
    {
        using TemporaryFolder folder = new();
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 1);
        TerrainTileKey a = new(4, 1, 1), b = new(4, 2, 2), c = new(4, 3, 3), d = new(4, 4, 4);
        cache.Store(a, Tile(a, 300));
        cache.Store(b, Tile(b, 300));
        cache.Store(c, Tile(c, 300));
        Assert.NotNull(Read(cache, a));

        // Room for three: the fourth takes the place of the least recently used, which is b now, not a.
        cache.Store(d, Tile(d, 300));
        Assert.True(cache.Contains(a));
        Assert.False(cache.Contains(b));
        Assert.True(cache.Contains(c));
        Assert.True(cache.Contains(d));
        Assert.Equal(900 * 1024, cache.Bytes);
    }

    [Fact]
    public void The_order_survives_a_restart_by_the_files_write_times()
    {
        using TemporaryFolder folder = new();
        TerrainTileKey a = new(4, 1, 1), b = new(4, 2, 2), c = new(4, 3, 3), d = new(4, 4, 4), e = new(4, 5, 5);
        string identityFolder;
        using (TerrainTileDiskCache first = Open(folder.Path, Current, 1))
        {
            first.Store(a, Tile(a, 300));
            first.Store(b, Tile(b, 300));
            first.Store(c, Tile(c, 300));
            identityFolder = first.Folder;
        }

        DateTime now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(Path.Combine(identityFolder, "4_1_1.ats"), now.AddHours(-1));
        File.SetLastWriteTimeUtc(Path.Combine(identityFolder, "4_2_2.ats"), now.AddHours(-3));
        File.SetLastWriteTimeUtc(Path.Combine(identityFolder, "4_3_3.ats"), now.AddHours(-2));

        using TerrainTileDiskCache cache = Open(folder.Path, Current, 1);
        cache.Store(d, Tile(d, 300));
        Assert.False(cache.Contains(b));
        Assert.True(cache.Contains(c));

        cache.Store(e, Tile(e, 300));
        Assert.False(cache.Contains(c));
        Assert.True(cache.Contains(a));
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public void An_older_identity_counts_against_the_quota_and_goes_first()
    {
        using TemporaryFolder folder = new();
        using (TerrainTileDiskCache old = Open(folder.Path, Old, 64))
            old.Store(new TerrainTileKey(4, 9, 9), Blob(700));

        using TerrainTileDiskCache cache = Open(folder.Path, Current, 1);
        Assert.Equal(0, cache.Count);
        Assert.Equal(700 * 1024, cache.Bytes);

        cache.Store(new TerrainTileKey(4, 1, 1), Blob(500));
        Assert.True(cache.Contains(new TerrainTileKey(4, 1, 1)));
        Assert.Equal(500 * 1024, cache.Bytes);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(folder.Path, Old)));
    }

    [Fact]
    public void A_file_that_is_not_a_sound_tile_is_never_served()
    {
        using TemporaryFolder folder = new();
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 64);
        TerrainTileKey key = new(4, 5, 6);
        cache.Store(key, Blob(10));

        byte[]? read = [];
        bool done = false;
        Assert.True(cache.TryRead(key, data => { read = data; done = true; }));
        Wait.Until(() => done, () => { }, "the read");

        Assert.Null(read);
        Assert.False(cache.Contains(key));
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void A_run_of_failed_writes_turns_the_cache_off_with_one_warning()
    {
        using TemporaryFolder folder = new();
        ConcurrentQueue<string> log = new();
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 64, log.Enqueue);
        TerrainTileKey kept = new(4, 1, 1);
        cache.Store(kept, Blob(10));
        Assert.True(cache.Contains(kept));

        Break(cache);
        for (int i = 0; i < TerrainTileDiskCache.FailedWritesBeforeOff - 1; i++)
            cache.Store(new TerrainTileKey(4, 10 + i, 1), Blob(10));

        Assert.False(cache.Disabled);
        Assert.True(cache.Contains(kept));

        cache.Store(new TerrainTileKey(4, 20, 1), Blob(10));
        Assert.True(cache.Disabled);
        Assert.False(cache.Contains(kept));
        Assert.False(cache.TryRead(kept, _ => { }));

        // Off means off: later writes are not tried, so the warning is not repeated.
        cache.Store(new TerrainTileKey(4, 21, 1), Blob(10));
        Assert.Single(log, line => line.Contains("tile disk cache off", StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains($"{TerrainTileDiskCache.FailedWritesBeforeOff} writes in a row failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_writes_with_a_good_one_between_them_leave_the_cache_on()
    {
        using TemporaryFolder folder = new();
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 64);

        Break(cache);
        for (int i = 0; i < TerrainTileDiskCache.FailedWritesBeforeOff - 1; i++)
            cache.Store(new TerrainTileKey(4, 10 + i, 1), Blob(10));

        Mend(cache);
        TerrainTileKey good = new(4, 1, 1);
        cache.Store(good, Blob(10));
        Assert.True(cache.Contains(good));

        Break(cache);
        for (int i = 0; i < TerrainTileDiskCache.FailedWritesBeforeOff - 1; i++)
            cache.Store(new TerrainTileKey(4, 20 + i, 1), Blob(10));

        Assert.False(cache.Disabled);
    }

    [Fact]
    public void A_read_is_answered_while_a_write_is_held_before_its_move()
    {
        using TemporaryFolder folder = new();
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 64);
        TerrainTileKey cached = new(4, 1, 1);
        TerrainTileKey slow = new(4, 2, 2);
        byte[] tile = Tile(cached, 10);
        byte[] first = Tile(slow, 10);
        cache.Store(cached, tile);

        // The first write of the slow tile stops, flushed but not yet in place, until released.
        using ManualResetEventSlim flushed = new();
        using ManualResetEventSlim release = new();
        int held = 0;
        cache.WriteFlushed = key =>
        {
            if (key == slow && Interlocked.Increment(ref held) == 1)
            {
                flushed.Set();
                _ = release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        Thread writer = new(() => cache.Store(slow, first));
        writer.Start();
        try
        {
            Assert.True(flushed.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

            // Nothing can read half of it: it is a temporary, and not yet known.
            Assert.False(cache.Contains(slow));
            Assert.False(File.Exists(Path.Combine(cache.Folder, "4_2_2.ats")));
            _ = Assert.Single(Directory.EnumerateFiles(cache.Folder, "*.tmp"));

            // Under one lock for both, this read would wait for the release.
            Assert.Equal(tile, Read(cache, cached));

            // A second write of the same tile does not wait, and does not collide with the first: it is skipped.
            byte[] again = Tile(slow, 10);
            Thread second = new(() => cache.Store(slow, again));
            second.Start();
            Assert.True(second.Join(TimeSpan.FromSeconds(20)));
            _ = Assert.Single(Directory.EnumerateFiles(cache.Folder, "*.tmp"));
        }
        finally
        {
            release.Set();
            writer.Join();
        }

        Assert.True(cache.Contains(slow));
        Assert.Equal(first, Read(cache, slow));
        Assert.Empty(Directory.EnumerateFiles(cache.Folder, "*.tmp"));
        Assert.Equal(20 * 1024, cache.Bytes);
    }

    [Fact]
    public void Caches_under_a_shared_parent_count_against_one_quota()
    {
        using TemporaryFolder parent = new();
        string otherWorld = Path.Combine(parent.Path, "another-world");
        string elsewhere = Plant(otherWorld, Old, "4_9_9.ats", 700);
        string young = Plant(otherWorld, Old, "4_8_8.ats.0123abcd.tmp", 1);
        string mine = Path.Combine(parent.Path, "this-world");

        // By default another root is none of this cache's business.
        using (TerrainTileDiskCache alone = Open(mine, Current, 1))
            Assert.Equal(0, alone.Bytes);

        using TerrainTileDiskCache cache = Open(mine, Current, 1, sharedParent: parent.Path);
        Assert.Equal(700 * 1024, cache.Bytes);

        // Another process may be writing it.
        Assert.True(File.Exists(young));

        TerrainTileKey key = new(4, 1, 1);
        cache.Store(key, Blob(500));
        Assert.True(cache.Contains(key));
        Assert.False(File.Exists(elsewhere));
        Assert.Equal(500 * 1024, cache.Bytes);
    }

    [Fact]
    public void Folders_unused_for_long_enough_are_removed_at_start_when_asked()
    {
        using TemporaryFolder folder = new();
        DateTime now = DateTime.UtcNow;
        string recentIdentity = TerrainTileIdentity.Hash(writer => writer.Write("recent"));
        string stale = Plant(folder.Path, Old, "4_9_9.ats", 10, now.AddDays(-100));
        string recent = Plant(folder.Path, recentIdentity, "4_9_9.ats", 10, now.AddDays(-10));
        string own = Plant(folder.Path, Current, "4_1_1.ats", 10, now.AddDays(-100));

        // By default nothing goes for age alone.
        using (TerrainTileDiskCache keeping = Open(folder.Path, Current, 64))
            Assert.Equal(30 * 1024, keeping.Bytes);

        Assert.True(File.Exists(stale));

        ConcurrentQueue<string> log = new();
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 64, log.Enqueue, unusedDays: 90);
        Assert.False(Directory.Exists(Path.Combine(folder.Path, Old)));
        Assert.True(File.Exists(recent));

        // This cache's own folder is never stale: it is in use.
        Assert.True(File.Exists(own));
        Assert.Equal(1, cache.Count);
        Assert.Equal(20 * 1024, cache.Bytes);
        Assert.Contains(log, line => line.Contains("removed 1 folder unused for 90 days", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stale_world_under_a_shared_parent_goes_whole_and_nothing_else_does()
    {
        using TemporaryFolder parent = new();
        DateTime old = DateTime.UtcNow.AddDays(-100);
        string deletedWorld = Path.Combine(parent.Path, "deleted-world");
        _ = Plant(deletedWorld, Old, "4_9_9.ats", 10, old);
        _ = Plant(deletedWorld, Old, "4_9_8.ats.0123abcd.tmp", 1, old);
        string keptWorld = Path.Combine(parent.Path, "kept-world");
        _ = Plant(keptWorld, Old, "4_9_9.ats", 10, old);
        string notes = Plant(keptWorld, "", "notes.txt", 1, old);
        string notACache = Plant(keptWorld, "not-a-cache", "4_1_1.ats", 10, old);

        using TerrainTileDiskCache cache = Open(Path.Combine(parent.Path, "this-world"), Current, 64, sharedParent: parent.Path, unusedDays: 90);

        // The deleted world's cache, and the folder it was in, now empty.
        Assert.False(Directory.Exists(deletedWorld));

        // Only a cache's own folders go; the root that holds something else stays, with it.
        Assert.False(Directory.Exists(Path.Combine(keptWorld, Old)));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(notACache));
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void A_write_replaces_a_copy_that_is_open_for_reading()
    {
        using TemporaryFolder folder = new();
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 64);
        TerrainTileKey key = new(4, 3, 3);
        cache.Store(key, Tile(key, 10));
        byte[] newer = Tile(key, 12);

        // Open as the cache's own read opens it. Windows will not rename over it, so the write takes the fallback.
        using (FileStream reading = new(Path.Combine(cache.Folder, "4_3_3.ats"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            cache.Store(key, newer);

        Assert.True(cache.Contains(key));
        Assert.Equal(newer, Read(cache, key));
        Assert.Equal(12 * 1024, cache.Bytes);
        Assert.Empty(Directory.EnumerateFiles(cache.Folder, "*.tmp"));
    }

    [Fact]
    public void A_temporary_the_scan_cannot_delete_is_counted_and_the_cache_starts()
    {
        using TemporaryFolder folder = new();
        DateTime old = DateTime.UtcNow.AddHours(-2);
        string tile = Plant(folder.Path, Current, "4_1_1.ats", 10, old);
        string stuck = Plant(folder.Path, Current, "4_2_2.ats.0123abcd.tmp", 5, old);

        // Held open without sharing deletion, as another process's write, or a virus scanner, holds one.
        using (FileStream holding = new(stuck, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using TerrainTileDiskCache cache = Open(folder.Path, Current, 64);
            Assert.False(cache.Disabled);
            Assert.True(File.Exists(stuck));
            Assert.Equal(1, cache.Count);
            Assert.Equal(15 * 1024, cache.Bytes);
        }

        // Next time it goes.
        using (TerrainTileDiskCache again = Open(folder.Path, Current, 64))
        {
            Assert.False(File.Exists(stuck));
            Assert.Equal(10 * 1024, again.Bytes);
        }

        Assert.True(File.Exists(tile));
    }

    [Fact]
    public void A_folder_deleted_while_the_cache_runs_is_made_again()
    {
        using TemporaryFolder folder = new();
        ConcurrentQueue<string> log = new();
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 64, log.Enqueue);
        TerrainTileKey before = new(4, 1, 1);
        cache.Store(before, Tile(before, 10));

        // The whole root, as a player clearing out ModData while the game runs would.
        Directory.Delete(folder.Path, recursive: true);
        for (int i = 0; i <= TerrainTileDiskCache.FailedWritesBeforeOff; i++)
        {
            TerrainTileKey key = new(4, 10 + i, 1);
            cache.Store(key, Tile(key, 10));
            Assert.True(cache.Contains(key));
        }

        Assert.False(cache.Disabled);
        Assert.True(Directory.Exists(cache.Folder));

        // The tile that went with the folder is a miss, not a fault.
        Assert.Null(Read(cache, before));
        Assert.False(cache.Contains(before));
        Assert.DoesNotContain(log, line => line.Contains("could not use", StringComparison.Ordinal));
    }

    [Fact]
    public void A_young_temporary_is_left_for_the_process_that_may_be_writing_it()
    {
        using TemporaryFolder folder = new();
        string young = Plant(folder.Path, Current, "4_1_1.ats.0123abcd.tmp", 5);
        string abandoned = Plant(folder.Path, Current, "4_2_2.ats.4567cdef.tmp", 5, DateTime.UtcNow.AddHours(-2));

        // As a dedicated server's would be, mid-write, when a game on the same data folder opens the same cache.
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 64);
        Assert.False(cache.Disabled);
        Assert.True(File.Exists(young));
        Assert.False(File.Exists(abandoned));
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void A_folder_with_a_write_under_way_is_not_removed_for_age()
    {
        using TemporaryFolder folder = new();
        string tile = Plant(folder.Path, Old, "4_9_9.ats", 10, DateTime.UtcNow.AddDays(-100));
        string writing = Plant(folder.Path, Old, "4_9_8.ats.0123abcd.tmp", 5);

        using TerrainTileDiskCache cache = Open(folder.Path, Current, 64, unusedDays: 90);
        Assert.True(File.Exists(tile));
        Assert.True(File.Exists(writing));
        Assert.Equal(10 * 1024, cache.Bytes);
    }

    [Fact]
    public void A_file_that_goes_while_it_is_scanned_is_skipped()
    {
        using TemporaryFolder folder = new();
        string here = Plant(folder.Path, Current, "4_1_1.ats", 3);

        Assert.True(TerrainTileDiskCache.TryMeasure(here, out long size, out _));
        Assert.Equal(3 * 1024, size);
        File.Delete(here);
        Assert.False(TerrainTileDiskCache.TryMeasure(here, out _, out _));
        Assert.False(TerrainTileDiskCache.TryMeasure(Path.Combine(folder.Path, "gone", "4_1_1.ats"), out _, out _));
    }

    [Fact]
    public void Another_identity_folder_that_cannot_be_listed_is_left_alone()
    {
        using TemporaryFolder folder = new();
        _ = Plant(folder.Path, Old, "4_9_9.ats", 10);
        _ = Plant(folder.Path, Current, "4_1_1.ats", 10);
        DirectoryInfo other = new(Path.Combine(folder.Path, Old));
        FileSystemAccessRule deny = new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        DirectorySecurity security = other.GetAccessControl();
        security.AddAccessRule(deny);
        other.SetAccessControl(security);
        try
        {
            using TerrainTileDiskCache cache = Open(folder.Path, Current, 64);
            Assert.False(cache.Disabled);
            Assert.Equal(1, cache.Count);
            Assert.Equal(10 * 1024, cache.Bytes);
        }
        finally
        {
            security = other.GetAccessControl();
            _ = security.RemoveAccessRule(deny);
            other.SetAccessControl(security);
        }
    }

    [Fact]
    public void A_cache_whose_own_folder_cannot_be_made_is_off()
    {
        using TemporaryFolder folder = new();
        File.WriteAllText(Path.Combine(folder.Path, Current), "");
        using TerrainTileDiskCache cache = Open(folder.Path, Current, 64);
        Assert.True(cache.Disabled);
        Assert.False(cache.Contains(new TerrainTileKey(4, 1, 1)));
    }
}
