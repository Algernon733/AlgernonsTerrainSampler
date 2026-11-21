#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>
/// Encoded tiles on disk, one file each under <c>root/identity/</c>. A different identity is a different folder, so
/// a world whose inputs change starts a fresh cache; older folders under the same root count against the quota until
/// they are evicted. Given a shared parent, the folders under every other root in it count too, so one quota bounds
/// them all; given a number of days, folders unused for that long are deleted whole at start.
/// </summary>
/// <remarks>
/// <para>The index is built on the cache's own thread at start, without reading a tile. Reads run on that same thread, so
/// a cached tile never waits behind a cold one being sampled. Writes come from the tile workers.</para>
/// <para>One lock guards the bookkeeping (which files there are, their sizes and order, the bytes they take) and is never
/// held for file work: a read, a write's temporary file, its flush and its move into place, and the deletion of evicted
/// files all happen outside it, so a read never waits behind a write. A path being written or deleted is marked busy
/// meanwhile, so two writes of one tile never collide, and nothing deletes a file another thread is putting in place.
/// Each write has a temporary of its own, moved into place whole, so a read sees the old copy or the new, never part of
/// one. A read shares deletion, so an eviction is never refused because of it.</para>
/// <para>Two processes can share these folders: a dedicated server and a game started from one data folder do. So a
/// temporary is taken for abandoned, and deleted at start, only once it is an hour old, in this cache's own folder as in
/// any other; a younger one may be the other process's write, not yet moved into place. A file the other process evicts
/// is a miss here, not a fault.</para>
/// <para>When the quota is full, the least recently used files go first, from any identity's folder, as many as the new
/// tile needs. The order is kept as reads and writes happen, so evicting sorts nothing; it is sorted once at start, by
/// the files' write times, which a read also sets.</para>
/// <para>A file that fails its checks is deleted and its tile sampled again. If the storage itself fails, the cache turns
/// itself off and the service carries on without it: when its own folder cannot be made or listed at start, or when
/// <see cref="FailedWritesBeforeOff"/> writes fail in a row (a full disk, a folder that cannot be made). A single failed
/// write between good ones is only reported, and a folder deleted while the cache runs is made again by the next write.
/// A file the start-up scan cannot read is skipped, and one it cannot delete is counted, for an eviction to try
/// again.</para>
/// </remarks>
internal sealed class TerrainTileDiskCache : IDisposable
{
    /// <summary>Writes that must fail in a row, with none succeeding between them, before the cache turns itself off.</summary>
    internal const int FailedWritesBeforeOff = 5;

    private const string Extension = ".ats";
    private const int MaxFiles = 65536;

    // A temporary this young may belong to a write another process is making now, in any folder, this cache's own among
    // them: a dedicated server and a game started from one data folder share these caches.
    private static readonly TimeSpan AbandonedTemporary = TimeSpan.FromHours(1);

    private readonly string root;
    private readonly string directory;
    private readonly string? sharedParent;
    private readonly TimeSpan unusedFor;
    private readonly long limit;
    private readonly Action<string>? log;
    private readonly object indexLock = new();
    private readonly ConcurrentDictionary<TerrainTileKey, long> index = new();

    // Every file counted against the quota, any identity's, with its size and its place in the order.
    private readonly Dictionary<string, (long Size, LinkedListNode<string> Node)> files = new(StringComparer.OrdinalIgnoreCase);

    // The same files, least recently used first. A read or a write moves a file to the end and an eviction takes from
    // the front, so nothing is sorted after the start-up scan.
    private readonly LinkedList<string> order = new();

    // Paths a thread is writing or deleting with no lock held. Nothing else writes or deletes them meanwhile.
    private readonly HashSet<string> busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Action> reads = new(256);
    private readonly Thread thread;
    private long bytes;
    private long pendingBytes;
    private int pendingFiles;
    private volatile bool ready;
    private volatile bool disabled;
    private int failedWrites;
    private int reportedFailure;
    private int disposed;

    /// <summary>Starts the cache, and its thread, which builds the index before anything is read or written.</summary>
    /// <param name="root">The folder of identity folders this cache keeps its own in. Every identity folder in it counts
    /// against the quota.</param>
    /// <param name="identity">This cache's identity: a SHA-256 in lower-case hex, as <see cref="TerrainTileIdentity"/>
    /// makes it.</param>
    /// <param name="megabytes">The quota, 1 to 65,536 MiB.</param>
    /// <param name="log">Where to report what happens; null for nowhere.</param>
    /// <param name="sharedParent">If given, every other folder in it is taken for the root of caches like this one (other
    /// worlds', other servers'), and their identity folders count against this quota too, least recently used first,
    /// whichever cache wrote them. Pass the folder that holds <paramref name="root"/>. Only tile files in hash-named
    /// folders are counted or deleted, and a root that cannot be read is left alone. Null, the default, counts
    /// <paramref name="root"/> alone.</param>
    /// <param name="unusedDays">If above 0, at start, an identity folder other than this cache's own, in any root it
    /// counts, whose tiles have not been read or written for this many days, and with no write under way, is deleted
    /// whole, and so is another root that leaves empty. 0, the default, deletes nothing for age: such folders go only as
    /// the quota needs room.</param>
    public TerrainTileDiskCache(string root, string identity, int megabytes, Action<string>? log = null, string? sharedParent = null, int unusedDays = 0)
    {
        if (!TerrainTileIdentity.IsValid(identity))
            throw new ArgumentException("Invalid cache identity.", nameof(identity));

        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        this.directory = Path.Combine(this.root, identity);
        this.sharedParent = sharedParent == null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(sharedParent));
        this.unusedFor = unusedDays > 0 ? TimeSpan.FromDays(unusedDays) : TimeSpan.Zero;
        this.limit = Math.Clamp(megabytes, 1, 65536) * 1024L * 1024L;
        this.log = log;
        this.thread = new Thread(this.Run) { IsBackground = true, Name = "Terrain Sampler tile disk cache" };
        this.thread.Start();
    }

    /// <summary>The index is built. Until then nothing is known to be on disk, so nothing should be sampled either.</summary>
    public bool Ready => this.ready;

    public bool Disabled => this.disabled;

    public int Count => this.index.Count;

    public long Bytes => Interlocked.Read(ref this.bytes);

    public string Folder => this.directory;

    /// <summary>
    /// For tests: called on the writing thread once a tile's temporary file is written and flushed, before it is moved
    /// into place, with no lock held.
    /// </summary>
    internal Action<TerrainTileKey>? WriteFlushed { get; set; }

    public bool Contains(TerrainTileKey key) => !this.disabled && this.index.ContainsKey(key);

    private string PathFor(TerrainTileKey key) => Path.Combine(this.directory, $"{key.Step}_{key.TileX}_{key.TileZ}{Extension}");

    private void Run()
    {
        this.Initialise();
        foreach (Action read in this.reads.GetConsumingEnumerable())
            read();
    }

    private void Initialise()
    {
        try
        {
            _ = Directory.CreateDirectory(this.directory);
            DateTime now = DateTime.UtcNow;
            List<(string Path, long Size, DateTime Used)> found = [];

            // The file work comes before the lock. Nothing reads or writes until the cache is ready, so nothing waits.
            // Only this cache's own folder must be readable. Anything else that is not, another root, another identity's
            // folder or a single file, is left alone.
            (int Folders, long Bytes) removed = this.Scan(this.root, now, found);
            foreach (string other in this.OtherRoots())
            {
                try
                {
                    (int Folders, long Bytes) more = this.Scan(other, now, found);
                    removed = (removed.Folders + more.Folders, removed.Bytes + more.Bytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            List<(string Path, long Size)> doomed = [];
            lock (this.indexLock)
            {
                // The write time is the order across restarts: a read sets it too.
                found.Sort((a, b) => a.Used.CompareTo(b.Used));
                foreach ((string Path, long Size, DateTime Used) file in found)
                {
                    this.Touch(file.Path, file.Size);
                    if (string.Equals(Path.GetDirectoryName(file.Path), this.directory, StringComparison.OrdinalIgnoreCase)
                        && TryKey(file.Path, out TerrainTileKey key) && file.Size > TerrainTileCodec.HeaderBytes)
                        this.index[key] = file.Size;
                }

                _ = this.Trim(0, doomed);
            }

            this.Delete(doomed);
            string shared = this.sharedParent == null ? "" : $", the quota shared with every cache under {this.sharedParent}";
            this.log?.Invoke($"tile disk cache ready: {this.Count} tiles, {this.Bytes / (1024.0 * 1024.0):0.0} MiB in {this.directory}{shared}");
            if (removed.Folders > 0)
                this.log?.Invoke($"tile disk cache removed {removed.Folders} folder{(removed.Folders == 1 ? "" : "s")} unused for {this.unusedFor.TotalDays:0} days: {removed.Bytes / (1024.0 * 1024.0):0.0} MiB");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.disabled = true;
            this.index.Clear();
            this.ReportFailure(ex);
        }
        finally
        {
            this.ready = true;
        }
    }

    // With a shared parent, every folder in it but this cache's own root; none if it cannot be read.
    private List<string> OtherRoots()
    {
        List<string> others = [];
        if (this.sharedParent == null)
            return others;

        try
        {
            foreach (string folder in Directory.EnumerateDirectories(this.sharedParent))
            {
                if (!string.Equals(folder, this.root, StringComparison.OrdinalIgnoreCase))
                    others.Add(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return others;
    }

    // One root's identity folders, at start: their files added to found, temporaries left by writes that never finished
    // deleted, and folders unused for too long deleted whole. A file that goes, or cannot be read or deleted, meanwhile is
    // skipped, and so is another identity's folder that cannot be listed: only this cache's own folder must be listed, or
    // the scan fails. Returns how many folders went for age, and their bytes.
    private (int Folders, long Bytes) Scan(string root, DateTime now, List<(string Path, long Size, DateTime Used)> found)
    {
        bool ownRoot = string.Equals(root, this.root, StringComparison.OrdinalIgnoreCase);
        (int Folders, long Bytes) removed = (0, 0);
        foreach (string folder in this.IdentityFolders(root, ownRoot))
        {
            bool current = string.Equals(folder, this.directory, StringComparison.OrdinalIgnoreCase);
            List<(string Path, long Size, DateTime Used)> tiles;
            DateTime used;
            try
            {
                tiles = ScanFolder(folder, now, out used);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && !current)
            {
                continue;
            }

            if (current || this.unusedFor == TimeSpan.Zero || now - used < this.unusedFor)
            {
                found.AddRange(tiles);
                continue;
            }

            // A world deleted, a server not joined again, inputs changed for good: not coming back.
            long freed = RemoveFolder(folder, tiles, found);
            removed = (removed.Folders + 1, removed.Bytes + freed);
        }

        // A root left empty goes too, unless it is this cache's own.
        if (!ownRoot && removed.Folders > 0)
        {
            try
            {
                Directory.Delete(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    // A root's identity folders: only caches' own, hash-named. For this cache's own root, its own folder is always among
    // them, even if the root cannot be listed; another root that cannot be listed throws.
    private List<string> IdentityFolders(string root, bool ownRoot)
    {
        List<string> folders = [];
        try
        {
            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                if (TerrainTileIdentity.IsValid(Path.GetFileName(folder)))
                    folders.Add(folder);
            }
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && ownRoot)
        {
        }

        if (ownRoot && !folders.Exists(folder => string.Equals(folder, this.directory, StringComparison.OrdinalIgnoreCase)))
            folders.Add(this.directory);

        return folders;
    }

    // One identity folder's files, each with its size and write time, and when the folder was last used: when its newest
    // tile was last read or written, or a write still under way began, or, holding neither, the folder's own time (an
    // eviction from it touches that but means nothing). Temporaries left by writes that never finished, under this name
    // or an older one, are deleted once they are an hour old, and not before: a younger one may be another process's
    // write, not yet moved into place. One that will not go stays among the files, counted, for an eviction to try
    // again. A file that goes, or cannot be read, meanwhile is skipped. Throws only if the folder itself cannot be listed.
    private static List<(string Path, long Size, DateTime Used)> ScanFolder(string folder, DateTime now, out DateTime used)
    {
        // Before anything in it is deleted, which would make it look new.
        used = Directory.GetLastWriteTimeUtc(folder);
        DateTime writing = DateTime.MinValue;
        List<(string Path, long Size, DateTime Used)> files = [];
        foreach (string temporary in Directory.EnumerateFiles(folder, "*" + Extension + "*.tmp"))
        {
            if (!TryMeasure(temporary, out long size, out DateTime written))
                continue;

            if (now - written < AbandonedTemporary)
            {
                writing = written > writing ? written : writing;
                continue;
            }

            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files.Add((temporary, size, written));
            }
        }

        bool anyTile = false;
        foreach (string path in Directory.EnumerateFiles(folder, "*" + Extension))
        {
            if (!TryMeasure(path, out long size, out DateTime written))
                continue;

            used = !anyTile || written > used ? written : used;
            anyTile = true;
            files.Add((path, size, written));
        }

        used = writing > used ? writing : used;
        return files;
    }

    /// <summary>A file's size and write time, or false if it has gone or cannot be read.</summary>
    internal static bool TryMeasure(string path, out long size, out DateTime written)
    {
        try
        {
            FileInfo file = new(path);
            size = file.Length;
            written = file.LastWriteTimeUtc;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            size = 0;
            written = default;
            return false;
        }
    }

    // Deletes an identity folder's files, as the scan found them, then the folder if that empties it. A temporary the scan
    // left, being young, is not touched: the folder stays with it. A file that will not go is added to found, to be counted
    // and evicted like any other. Returns the bytes deleted.
    private static long RemoveFolder(string folder, List<(string Path, long Size, DateTime Used)> tiles, List<(string Path, long Size, DateTime Used)> found)
    {
        long freed = 0;
        foreach ((string Path, long Size, DateTime Used) tile in tiles)
        {
            try
            {
                File.Delete(tile.Path);
                freed += tile.Size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                found.Add(tile);
            }
        }

        try
        {
            Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something else is in it, or still open: the folder stays.
        }

        return freed;
    }

    private static bool TryKey(string path, out TerrainTileKey key)
    {
        key = default;
        string[] parts = Path.GetFileNameWithoutExtension(path).Split('_');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int step) || !int.TryParse(parts[1], out int x)
            || !int.TryParse(parts[2], out int z) || !TerrainTileKey.IsValidStep(step) || x < 0 || z < 0)
            return false;

        key = new TerrainTileKey(step, x, z);
        return true;
    }

    /// <summary>
    /// Queues a read. <paramref name="done"/> runs on the cache's thread with the tile's bytes, already checked, or null
    /// if the file was missing or damaged (and is now gone). False if the read could not be queued; try again later.
    /// </summary>
    public bool TryRead(TerrainTileKey key, Action<byte[]?> done)
    {
        if (!this.ready || this.disabled || !this.index.ContainsKey(key))
            return false;

        try
        {
            return this.reads.TryAdd(() => done(this.Load(key)));
        }
        catch (InvalidOperationException)
        {
            return false; // Disposed.
        }
    }

    // On the cache's own thread. The file is read with no lock held.
    private byte[]? Load(TerrainTileKey key)
    {
        string path = this.PathFor(key);
        try
        {
            byte[] data = this.ReadFile(path);
            if (!TerrainTileCodec.Verify(data, key))
                throw new InvalidDataException($"{path} is damaged or names another tile.");

            // Most recently used now, here and in the write time, which is the order across restarts. Failing to update
            // the time must not stop a sound tile from being served.
            lock (this.indexLock)
            {
                if (this.files.TryGetValue(path, out (long Size, LinkedListNode<string> Node) file))
                {
                    this.order.Remove(file.Node);
                    this.order.AddLast(file.Node);
                }
            }

            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return data;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            List<(string Path, long Size)> doomed = [];
            lock (this.indexLock)
            {
                // A write is putting a new copy in place, or an eviction is deleting this one: either way the file is
                // changing under the read, and whoever is changing it keeps the index.
                if (this.busy.Contains(path))
                    return null;

                _ = this.index.TryRemove(key, out _);
                if (this.Forget(path, out long size))
                {
                    _ = this.busy.Add(path);
                    doomed.Add((path, size));
                }
            }

            this.Delete(doomed);

            // A file that has simply gone (evicted by another process sharing the folder, say) is a miss, not a fault.
            if (ex is not (FileNotFoundException or DirectoryNotFoundException))
                this.ReportFailure(ex);

            return null;
        }
    }

    // Shares deletion, so an eviction, or a write putting a newer copy in place, is not refused because of this read.
    private byte[] ReadFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize: 1);
        if (stream.Length > this.limit)
            throw new InvalidDataException($"{path} is larger than the whole cache.");

        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    /// <summary>
    /// Writes a tile, replacing any older copy. Called from the tile workers. A write of a tile that is already being
    /// written is skipped: the other one is putting the same bytes in place.
    /// </summary>
    public void Store(TerrainTileKey key, byte[] encoded)
    {
        if (!this.ready || this.disabled || Volatile.Read(ref this.disposed) != 0 || encoded.Length > this.limit)
            return;

        string path = this.PathFor(key);
        List<(string Path, long Size)> doomed = [];
        bool room;
        lock (this.indexLock)
        {
            if (this.disabled || !this.busy.Add(path))
                return;

            // An older copy stays counted until the new one replaces it, so the new one needs room only for what it adds.
            long replacing = this.files.TryGetValue(path, out (long Size, LinkedListNode<string> Node) older) ? older.Size : 0;
            room = this.Trim(encoded.Length - replacing, doomed);
            if (room)
            {
                this.pendingBytes += encoded.Length;
                this.pendingFiles++;
            }
            else
            {
                _ = this.busy.Remove(path);
            }
        }

        this.Delete(doomed);
        if (!room)
            return;

        // A name of its own, so no other write, of this tile or another, can touch it before it is moved into place.
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        Exception? failure = null;
        try
        {
            using (FileStream stream = this.CreateTemporary(temporary))
            {
                stream.Write(encoded);
                stream.Flush(flushToDisk: true);
            }

            this.WriteFlushed?.Invoke(key);
            MoveIntoPlace(temporary, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex;
            try
            {
                File.Delete(temporary);
            }
            catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
            {
            }
        }

        lock (this.indexLock)
        {
            _ = this.busy.Remove(path);
            this.pendingBytes -= encoded.Length;
            this.pendingFiles--;
            if (failure == null)
            {
                this.Touch(path, encoded.Length);
                if (!this.disabled)
                    this.index[key] = encoded.Length;

                this.failedWrites = 0;
            }
            else if (++this.failedWrites >= FailedWritesBeforeOff)
            {
                this.TurnOff($"{this.failedWrites} writes in a row failed, the last with: {failure.Message}");
            }
        }

        if (failure != null)
            this.ReportFailure(failure);
    }

    // Something may delete this cache's folder while the game runs: a player clearing ModData, a disk cleaner. The write
    // makes it again, once, and only one that fails after that counts as a failure. The tiles that went with it stay in
    // the index until a read misses them, quietly, or an eviction finds them gone.
    private FileStream CreateTemporary(string temporary)
    {
        try
        {
            return new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (DirectoryNotFoundException)
        {
            _ = Directory.CreateDirectory(this.directory);
            return new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
    }

    // The rename is atomic, so a read sees the old copy or the new. Windows refuses to rename over a file that is open,
    // even by a read that shares deletion, but lets it be deleted, which frees the name: that is the fallback. A read
    // that opens the file in between finds nothing, and its tile is taken from memory or sampled again.
    private static void MoveIntoPlace(string temporary, string path)
    {
        try
        {
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(temporary) && File.Exists(path))
        {
            File.Delete(path);
            File.Move(temporary, path, overwrite: true);
        }
    }

    // Least recently used first, any identity's folder, never a file that is busy (the one being replaced among them),
    // and only as many as the write needs: taken from the front of the order, with no sort. Each one chosen is
    // forgotten, marked busy and added to doomed, for the caller to delete with no lock held. Caller holds the lock.
    private bool Trim(long needed, List<(string Path, long Size)> doomed)
    {
        LinkedListNode<string>? node = this.order.First;
        while (node != null && !this.Fits(needed))
        {
            LinkedListNode<string>? next = node.Next;
            string path = node.Value;
            if (!this.busy.Contains(path) && this.Forget(path, out long size))
            {
                _ = this.busy.Add(path);
                doomed.Add((path, size));
            }

            node = next;
        }

        return this.Fits(needed);
    }

    // Writes under way count as if they were done, so workers writing at once cannot overrun the quota between them.
    // Caller holds the lock.
    private bool Fits(long needed) =>
        this.bytes + this.pendingBytes + needed <= this.limit && this.files.Count + this.pendingFiles < MaxFiles;

    // Counts a file as the most recently used, or moves it there with its new size. Caller holds the lock.
    private void Touch(string path, long size)
    {
        if (this.files.TryGetValue(path, out (long Size, LinkedListNode<string> Node) file))
        {
            this.bytes -= file.Size;
            this.order.Remove(file.Node);
            this.order.AddLast(file.Node);
            this.files[path] = (size, file.Node);
        }
        else
        {
            this.files[path] = (size, this.order.AddLast(path));
        }

        this.bytes += size;
    }

    // Drops a file from the bookkeeping, and its tile from the index if it is one of this identity's. Caller holds the lock.
    private bool Forget(string path, out long size)
    {
        size = 0;
        if (!this.files.Remove(path, out (long Size, LinkedListNode<string> Node) file))
            return false;

        this.order.Remove(file.Node);
        this.bytes -= file.Size;
        size = file.Size;
        if (string.Equals(Path.GetDirectoryName(path), this.directory, StringComparison.OrdinalIgnoreCase) && TryKey(path, out TerrainTileKey key))
            _ = this.index.TryRemove(key, out _);

        return true;
    }

    // Deletes files already forgotten and marked busy, with no lock held, then clears their marks. One that will not go
    // is counted again, as the least recently used, so the next eviction tries it first.
    private void Delete(List<(string Path, long Size)> doomed)
    {
        if (doomed.Count == 0)
            return;

        List<(string Path, long Size)>? kept = null;
        foreach ((string Path, long Size) file in doomed)
        {
            try
            {
                File.Delete(file.Path);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(file.Path))
            {
                (kept ??= []).Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Its folder has gone, and the file with it.
            }
        }

        lock (this.indexLock)
        {
            foreach ((string Path, long Size) file in doomed)
                _ = this.busy.Remove(file.Path);

            if (kept == null)
                return;

            foreach ((string Path, long Size) file in kept)
            {
                if (this.files.ContainsKey(file.Path))
                    continue;

                this.files[file.Path] = (file.Size, this.order.AddFirst(file.Path));
                this.bytes += file.Size;
            }
        }
    }

    // Caller holds the lock. What is on disk stays there, uncounted, for the next start to find.
    private void TurnOff(string why)
    {
        if (this.disabled)
            return;

        this.disabled = true;
        this.index.Clear();
        this.log?.Invoke($"tile disk cache off: {why}. Tiles will be sampled again rather than read from or written to {this.directory}.");
    }

    private void ReportFailure(Exception ex)
    {
        if (Interlocked.Exchange(ref this.reportedFailure, 1) == 0)
            this.log?.Invoke($"tile disk cache could not use a file; affected tiles will be sampled again: {ex.Message}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        this.reads.CompleteAdding();
        this.thread.Join();
        this.reads.Dispose();
    }
}
