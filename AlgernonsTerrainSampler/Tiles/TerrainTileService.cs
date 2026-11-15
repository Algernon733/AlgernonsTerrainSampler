#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>What the service needs to know about whoever wants a tile.</summary>
/// <param name="Allowed">Whether they may ask at all.</param>
/// <param name="DeniedReason">Why not, when they may not.</param>
/// <param name="Limited">Whether the distance limit applies: true for players, false for mods on the server itself.</param>
/// <param name="X">The player's position, for the distance limit.</param>
/// <param name="Z">The player's position, for the distance limit.</param>
internal readonly record struct TerrainTileOwner(bool Allowed, string DeniedReason, bool Limited, double X, double Z)
{
    public static TerrainTileOwner Unlimited => new(true, "", false, 0, 0);

    public static TerrainTileOwner Denied(string reason) => new(false, reason, false, 0, 0);
}

/// <summary>
/// Each client's tiles in the sender. The service hands a client a tile only while its queue has room, so the sender
/// holds the bytes of a few tiles a client, whatever the client does; the rest stay wanted in the service, under its own
/// memory budget. Queued tiles count against the owner's <see cref="TerrainTileService.Settings.MaxWantedTiles"/>, and
/// leave the queue when the owner stops wanting them, or may no longer have them.
/// </summary>
internal interface ITerrainTileQueue
{
    /// <summary>How many more tiles the owner can be handed now; unlimited for one the queue does not serve, such as a mod on the server.</summary>
    int Room(string owner);

    /// <summary>Tiles queued for the owner that have not started on their way.</summary>
    int Count(string owner);

    /// <summary>
    /// Whether the owner will have the tile without asking again: queued, being sent, or sent and not yet acknowledged,
    /// and not dropped by the owner since it was handed over.
    /// </summary>
    bool OnItsWay(string owner, TerrainTileKey key);

    /// <summary>A copy of the owner's queue, oldest first.</summary>
    IReadOnlyList<TerrainTileKey> Keys(string owner);

    /// <summary>
    /// Takes the tile out of the owner's queue; whether it was there. One already on its way is finished, but no longer
    /// counts as on its way: the owner may have let its first pieces go, so asking again sends it again.
    /// </summary>
    bool Withdraw(string owner, TerrainTileKey key);

    /// <summary>As <see cref="Withdraw"/>, for every tile of the owner's: when it resets its wants or leaves.</summary>
    void WithdrawAll(string owner);
}

/// <summary>
/// The server's tile scheduler and memory cache. Owners (players by their UID, mods on the server by a name of their
/// own) say which tiles they want, at what priority; the service answers each one once, from memory, from disk or by
/// sampling, and one tile is only ever made once however many owners want it.
/// </summary>
/// <remarks>
/// <para>An owner is handed a tile only while it has room for it (<see cref="ITerrainTileQueue.Room"/>); until then the
/// tile stays wanted, kept in memory while the memory budget allows, and otherwise fetched again, from disk or by sampling
/// it again, when the owner has room. Fetching for an owner counts against its room too, so a client that takes nothing
/// costs memory only within the one budget. With a disk cache, tiles nobody has made are made ahead of room, since they
/// land on disk; without one, only as an owner has room for them.</para>
/// <para>Everything here runs on the server's main thread except the sampling, encoding and disk writes, which run on the
/// workers, and the disk reads, which run on the disk cache's thread. Results cross back through queues drained in
/// <see cref="Tick"/>. Nothing here touches the game, so it can be tested without one.</para>
/// </remarks>
internal sealed class TerrainTileService : IDisposable
{
    /// <summary>
    /// Failed tiles in a row after which the sampler itself is checked, on a column it sampled before. If that fails too,
    /// the sampler is broken and every tile refused; if not, the tiles failed on their own, in an area that cannot be
    /// sampled, and are only refused.
    /// </summary>
    public const int BrokenAfterFailures = 8;

    /// <summary>How often a broken sampler is checked again, and how often repeated failures are summed up in the log.</summary>
    public const long RetryMilliseconds = 60000;

    // Distinct failures whose message is kept apart; past that, failures are told apart by their type alone.
    private const int MaxFailureKinds = 32;

    public sealed record Settings(
        int Workers,
        int MemoryCacheMegabytes,
        int MaxWantedTiles,
        int MaxDistancePerStep,
        int MaxDistance = 32768,
        bool SampleOnMainThread = false,
        double MainThreadBudgetMilliseconds = 4);

    private readonly int mapSizeX;
    private readonly int mapSizeZ;
    private readonly Func<int, int, TerrainColumnSample> sample;
    private readonly TerrainTileDiskCache? disk;
    private readonly Settings settings;
    private readonly Func<string, TerrainTileOwner> owners;
    private readonly Action<string, TerrainTileKey, byte[]> ready;
    private readonly Action<string, TerrainTileKey, string> refused;
    private readonly Action<string>? log;
    private readonly Func<long> clock;
    private readonly ITerrainTileQueue? queue;
    private readonly long memoryLimit;

    private readonly Dictionary<TerrainTileKey, Entry> entries = [];
    private readonly Dictionary<string, HashSet<TerrainTileKey>> wanted = new(StringComparer.Ordinal);
    private readonly List<string> ownerOrder = [];
    private readonly ConcurrentQueue<(TerrainTileKey Key, byte[]? Data)> reads = new();
    private readonly ConcurrentQueue<TerrainTileJob> finished = new();
    private readonly TerrainTileWorkers workers;
    private TerrainTileJob? mainThreadJob;
    private int running;
    private int ownerCursor;
    private int feedCursor;
    private long memoryBytes;
    private long serial;
    private long nextOwnerCheck;
    private volatile int throttle;
    private bool disposed;

    // Failures: each kind's repeats since it was last logged, the run of them, the column that shows whether the sampler
    // itself works (where a tile was last made, the world's centre until one is), and the checks of it.
    private readonly Dictionary<string, int> failureRepeats = new(StringComparer.Ordinal);
    private int failuresInARow;
    private long nextFailureSummary;
    private long nextCheck;
    private long nextAreaNote;
    private (int X, int Z) goodColumn;

    public TerrainTileService(
        int mapSizeX,
        int mapSizeZ,
        Func<int, int, TerrainColumnSample> sample,
        TerrainTileDiskCache? disk,
        Settings settings,
        Func<string, TerrainTileOwner> owners,
        Action<string, TerrainTileKey, byte[]> ready,
        Action<string, TerrainTileKey, string> refused,
        Action<string>? log = null,
        Func<long>? clock = null,
        ITerrainTileQueue? queue = null)
    {
        this.mapSizeX = mapSizeX;
        this.mapSizeZ = mapSizeZ;
        this.sample = sample;
        this.disk = disk;
        this.settings = settings;
        this.owners = owners;
        this.ready = ready;
        this.refused = refused;
        this.log = log;
        this.clock = clock ?? (() => Environment.TickCount64);
        this.queue = queue;
        this.memoryLimit = Math.Clamp(settings.MemoryCacheMegabytes, 1, 4096) * 1024L * 1024L;
        this.goodColumn = (mapSizeX / 2, mapSizeZ / 2);
        this.workers = new TerrainTileWorkers(settings.Workers, this.RunJob);
    }

    public long Generated { get; private set; }
    public long GeneratedBytes { get; private set; }
    public long MemoryHits { get; private set; }
    public long DiskHits { get; private set; }
    public long Failures { get; private set; }
    public long Refusals { get; private set; }
    public double GenerateMilliseconds { get; private set; }
    public int Running => this.running;

    /// <summary>
    /// Why every tile is refused, after <see cref="BrokenAfterFailures"/> failed tiles in a row and a failed check of the
    /// sampler itself; null while the sampler works. The check is made again every <see cref="RetryMilliseconds"/>, and
    /// the first that passes, or the first tile made, clears it.
    /// </summary>
    public string? FailureReason { get; private set; }

    public int MemoryTiles => this.entries.Values.Count(e => e.Data != null);
    public long MemoryBytes => this.memoryBytes;
    public int Owners => this.wanted.Count;

    /// <summary>Tiles some owner wants that are neither in memory, nor being read, nor being made.</summary>
    public int Waiting => this.entries.Values.Count(e => e.Wanted.Count > 0 && e.Data == null && !e.Reading && !e.Generating);

    /// <summary>Tiles the owner wants that it has not been handed: being made, made and waiting for room, or neither.</summary>
    public int WantedBy(string owner) => this.wanted.TryGetValue(owner, out HashSet<TerrainTileKey>? keys) ? keys.Count : 0;

    // Whether the disk cache can be read; until its index is built nothing is sampled either.
    private bool DiskReadable => this.disk != null && this.disk.Ready && !this.disk.Disabled;

    private bool DiskIndexing => this.disk != null && !this.disk.Ready;

    private int RoomFor(string owner) => this.queue?.Room(owner) ?? int.MaxValue;

    /// <summary>
    /// Asks for a tile, or changes its priority if it is already wanted. Lower numbers come first. The answer is
    /// <c>ready</c> or <c>refused</c>, perhaps at once, perhaps in a later tick: <c>ready</c> when the owner has room.
    /// </summary>
    public void Want(string owner, TerrainTileKey key, int priority)
    {
        if (!this.disposed)
            this.Want(owner, key, priority, this.owners(owner));
    }

    private void Want(string owner, TerrainTileKey key, int priority, TerrainTileOwner info)
    {
        if (this.FailureReason != null)
        {
            this.Refuse(owner, key, this.FailureReason);
            return;
        }

        if (!key.IsValid(this.mapSizeX, this.mapSizeZ))
        {
            this.Refuse(owner, key, "That tile is outside the world, or its step is not a power of two from 1 to 64.");
            return;
        }

        if (!info.Allowed)
        {
            this.Refuse(owner, key, info.DeniedReason);
            return;
        }

        if (info.Limited && this.TooFar(key, info))
        {
            this.Refuse(owner, key, this.TooFarReason(key));
            return;
        }

        if (!this.wanted.TryGetValue(owner, out HashSet<TerrainTileKey>? keys))
        {
            keys = [];
            this.wanted[owner] = keys;
            this.ownerOrder.Add(owner);
        }

        if (!keys.Contains(key))
        {
            // Queued, being sent or awaiting acknowledgement: on its way, so this is only a change of priority, too late
            // to matter. Neither sent again nor counted again; a client asks again this way as it comes closer.
            if (this.queue?.OnItsWay(owner, key) == true)
                return;

            if (keys.Count + (this.queue?.Count(owner) ?? 0) >= this.settings.MaxWantedTiles)
            {
                this.Refuse(owner, key, string.Create(CultureInfo.InvariantCulture, $"More than {this.settings.MaxWantedTiles} tiles wanted at once."));
                return;
            }
        }

        if (!this.entries.TryGetValue(key, out Entry? entry))
        {
            entry = new Entry(key);
            this.entries[key] = entry;
        }

        entry.Wanted[owner] = priority;
        _ = keys.Add(key);

        // In memory: handed over now if the owner has room. Otherwise the next tick's Feed reads it, or a worker makes it.
        if (entry.Data != null && this.RoomFor(owner) > 0)
        {
            this.MemoryHits++;
            entry.Used = ++this.serial;
            this.HandOver(owner, entry);
        }
    }

    /// <summary>
    /// No longer wants the tile. A tile already being made is finished and kept: it is cheap to keep and may be wanted
    /// again. One answered and still queued for the owner leaves the queue.
    /// </summary>
    public void Drop(string owner, TerrainTileKey key)
    {
        _ = this.queue?.Withdraw(owner, key);
        if (this.wanted.TryGetValue(owner, out HashSet<TerrainTileKey>? keys))
            _ = keys.Remove(key);

        if (this.entries.TryGetValue(key, out Entry? entry))
        {
            _ = entry.Wanted.Remove(owner);
            this.Forget(entry);
        }
    }

    /// <summary>Drops everything the owner wants, and everything queued for it, as when a player leaves.</summary>
    public void DropOwner(string owner)
    {
        this.queue?.WithdrawAll(owner);
        if (!this.wanted.Remove(owner, out HashSet<TerrainTileKey>? keys))
            return;

        _ = this.ownerOrder.Remove(owner);
        foreach (TerrainTileKey key in keys)
        {
            if (this.entries.TryGetValue(key, out Entry? entry))
            {
                _ = entry.Wanted.Remove(owner);
                this.Forget(entry);
            }
        }
    }

    /// <summary>
    /// A client's want message: a reset, then its drops, then its adds, at most <see cref="TerrainTileProtocol.MaxKeysPerWant"/>
    /// of each. A key added more than once counts once, at its first priority. Each add is answered once; the sender
    /// gathers the refusals by reason, so adds past the limit, say, come back as one refusal.
    /// </summary>
    public void Apply(string owner, TerrainTileWant want)
    {
        if (this.disposed)
            return;

        if (want.Reset)
            this.DropOwner(owner);

        foreach (TerrainTileKey key in want.Drops())
            this.Drop(owner, key);

        List<(TerrainTileKey Key, int Priority)> adds = want.Adds();
        if (adds.Count == 0)
            return;

        TerrainTileOwner info = this.owners(owner);
        foreach ((TerrainTileKey key, int priority) in adds)
            this.Want(owner, key, priority, info);
    }

    /// <summary>The game process's load, from which the workers decide how much to give way.</summary>
    public void UpdateLoad(double processCpuPercent, long tickIntervalMilliseconds)
        => this.throttle = processCpuPercent >= 75 ? 20 : tickIntervalMilliseconds > 100 ? 10 : 0;

    public void Tick()
    {
        if (this.disposed)
            return;

        while (this.reads.TryDequeue(out (TerrainTileKey Key, byte[]? Data) read))
        {
            if (!this.entries.TryGetValue(read.Key, out Entry? entry))
                continue;

            entry.Reading = false;
            if (read.Data != null)
            {
                this.DiskHits++;
                this.Keep(entry, read.Data);
                this.Deliver(entry);
            }
            else
            {
                this.Forget(entry); // Missing or damaged: made again below if it is still wanted.
            }
        }

        while (this.finished.TryDequeue(out TerrainTileJob? job))
            this.Finish(job);

        if (this.mainThreadJob != null)
            this.AdvanceMainThreadJob();

        long now = this.clock();
        if (now >= this.nextOwnerCheck)
        {
            this.nextOwnerCheck = now + 2000;
            this.CheckOwners();
        }

        if (now >= this.nextFailureSummary)
        {
            this.nextFailureSummary = now + RetryMilliseconds;
            this.SumUpFailures();
        }

        this.Feed();
        this.StartJobs(now);
        this.Trim();
    }

    private void Finish(TerrainTileJob job)
    {
        this.running--;
        if (!this.entries.TryGetValue(job.Key, out Entry? entry))
            return;

        entry.Generating = false;
        if (job.Error == null && job.Result != null)
        {
            this.Generated++;
            this.GeneratedBytes += job.Result.Length;
            this.GenerateMilliseconds += job.SampleMilliseconds;
            this.failuresInARow = 0;
            this.goodColumn = (job.Key.OriginX, job.Key.OriginZ);
            if (this.FailureReason != null)
                this.Recover();

            this.Keep(entry, job.Result);
            this.Deliver(entry);
            return;
        }

        if (job.Error is OperationCanceledException)
        {
            this.Forget(entry);
            return;
        }

        // Never kept: a failure is an answer for now, not for good. The next want samples it again.
        this.Failures++;
        this.failuresInARow++;
        this.LogFailure(job.Key, job.Error);
        string message = job.Error?.GetBaseException().Message ?? "no error given";
        string reason = $"The terrain sampler failed on this tile: {message}";
        foreach (string owner in entry.Wanted.Keys.ToArray())
        {
            if (this.wanted.TryGetValue(owner, out HashSet<TerrainTileKey>? keys))
                _ = keys.Remove(job.Key);

            this.Refuse(owner, job.Key, reason);
        }

        entry.Wanted.Clear();
        this.Forget(entry);
        if (this.failuresInARow < BrokenAfterFailures || this.FailureReason != null)
            return;

        // A run of failures: broken, or only an area that cannot be sampled (a world edge, a generator that throws in
        // some regions)? The column where a tile was last made says which.
        this.failuresInARow = 0;
        string? broken = this.CheckSampler();
        if (broken != null)
        {
            this.Break(broken);
            return;
        }

        long now = this.clock();
        if (now >= this.nextAreaNote)
        {
            this.nextAreaNote = now + RetryMilliseconds;
            this.log?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"{BrokenAfterFailures} tiles in a row failed, but the sampler still works at {this.goodColumn.X}, {this.goodColumn.Z}, so only those tiles are refused."));
        }
    }

    // Samples the one column: null if it works, why not if it fails. On the main thread, as one column costs little.
    private string? CheckSampler()
    {
        try
        {
            _ = this.sample(this.goodColumn.X, this.goodColumn.Z);
            return null;
        }
        catch (Exception ex)
        {
            return ex.GetBaseException().Message;
        }
    }

    // A failure's stack trace is logged the first time it is seen; after that it is counted, and summed up each minute.
    private void LogFailure(TerrainTileKey key, Exception? error)
    {
        Exception? root = error?.GetBaseException();
        string kind = root == null ? "no error given" : $"{root.GetType().FullName}: {root.Message}";
        if (!this.failureRepeats.ContainsKey(kind) && this.failureRepeats.Count >= MaxFailureKinds)
            kind = root?.GetType().FullName ?? kind; // Messages that differ every time, such as ones naming a column.

        if (this.failureRepeats.TryGetValue(kind, out int repeats))
        {
            this.failureRepeats[kind] = repeats + 1;
            return;
        }

        this.failureRepeats[kind] = 0;
        this.log?.Invoke($"could not make {key}, and will count rather than log this failure if it comes again: {error}");
    }

    private void SumUpFailures()
    {
        foreach ((string kind, int repeats) in this.failureRepeats.Where(pair => pair.Value > 0).ToArray())
        {
            this.log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"the same failure {repeats} more time(s) since it was last logged: {kind}"));
            this.failureRepeats[kind] = 0;
        }
    }

    // The sampler is broken, as when Watersheds turned its delegation off after an error and the strict path refuses every
    // column: it failed where it worked before. Everything is refused, with why, until it works again.
    private void Break(string message)
    {
        this.FailureReason = string.Create(
            CultureInfo.InvariantCulture,
            $"The terrain sampler failed on {BrokenAfterFailures} tiles in a row and on a column it sampled before ({message}), so tiles are refused until it works again; the server checks every minute.");
        this.log?.Invoke($"terrain tiles unavailable: {this.FailureReason}");
        foreach (string owner in this.ownerOrder.ToArray())
        {
            foreach (TerrainTileKey key in this.wanted[owner].ToArray())
                this.RefuseWanted(owner, key, this.FailureReason);
        }

        this.nextCheck = this.clock() + RetryMilliseconds;
    }

    private void Recover()
    {
        this.FailureReason = null;
        this.failuresInARow = 0;
        this.SumUpFailures();
        this.failureRepeats.Clear(); // A failure that comes back later is logged in full again.
        this.log?.Invoke("the terrain sampler works again: serving tiles.");
    }

    private void AdvanceMainThreadJob()
    {
        TerrainTileJob job = this.mainThreadJob!;
        try
        {
            job.Sample(this.sample, this.mapSizeX, this.mapSizeZ, this.settings.MainThreadBudgetMilliseconds, () => 0, this.workers.Token);
        }
        catch (Exception ex)
        {
            job.Error = ex;
        }

        if (job.Sampled || job.Error != null)
        {
            this.mainThreadJob = null;
            if (job.Error != null)
                this.finished.Enqueue(job);
            else
                this.workers.Submit(job); // Encoding and the disk write still happen off the main thread.
        }
    }

    // Owners whose permission went or who moved away lose what they can no longer have, queued for them or not.
    private void CheckOwners()
    {
        foreach (string owner in this.ownerOrder.ToArray())
        {
            TerrainTileOwner info = this.owners(owner);
            if (info.Allowed && !info.Limited)
                continue;

            foreach (TerrainTileKey key in this.wanted[owner].Concat(this.queue?.Keys(owner) ?? []).ToArray())
            {
                if (!info.Allowed)
                    this.RefuseWanted(owner, key, info.DeniedReason);
                else if (this.TooFar(key, info))
                    this.RefuseWanted(owner, key, this.TooFarReason(key));
            }
        }
    }

    private void RefuseWanted(string owner, TerrainTileKey key, string reason)
    {
        this.Drop(owner, key);
        this.Refuse(owner, key, reason);
    }

    // Each owner with room, in turn, gets its most urgent tiles: handed over if in memory, read if on disk, made if neither,
    // as many as it has room for, counting those already being read or made. One tile to make for each owner in turn.
    private void Feed()
    {
        bool readable = this.DiskReadable;
        bool readsFull = false;
        int count = this.ownerOrder.Count;
        Dictionary<string, Queue<Entry>> toMake = new(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string owner = this.ownerOrder[(this.feedCursor + i) % count];
            HashSet<TerrainTileKey> keys = this.wanted[owner];
            int room = this.RoomFor(owner);
            if (room <= 0 || keys.Count == 0)
                continue;

            Queue<Entry> make = new();
            foreach (Entry entry in keys.Select(key => this.entries[key]).OrderBy(entry => entry.Wanted[owner]).ToArray())
            {
                if (room <= 0)
                    break;

                if (entry.Data != null)
                {
                    entry.Used = ++this.serial;
                    this.HandOver(owner, entry);
                    room--;
                }
                else if (entry.Reading || entry.Generating)
                {
                    room--;
                }
                else if (readable && this.disk!.Contains(entry.Key))
                {
                    if (!readsFull && this.TryRead(entry))
                        room--;
                    else
                        readsFull = true; // The read queue is full; the rest wait for the next tick.
                }
                else if ((entry.Evicted || !readable) && !this.DiskIndexing)
                {
                    make.Enqueue(entry); // Dropped from memory and disk, or there is no disk to make it ahead onto.
                    room--;
                }
            }

            if (make.Count > 0)
                toMake[owner] = make;
        }

        this.feedCursor = count > 0 ? (this.feedCursor + 1) % count : 0;

        // One tile to make for each owner in turn, from the owner after the last one a worker was given to.
        int capacity = this.FreeWorkers();
        while (capacity > 0 && toMake.Count > 0)
        {
            this.ownerCursor %= count;
            string owner = this.ownerOrder[this.ownerCursor++];
            if (!toMake.TryGetValue(owner, out Queue<Entry>? make))
                continue;

            Entry? next = null;
            while (next == null && make.TryDequeue(out Entry? candidate))
            {
                if (!candidate.Generating)
                    next = candidate; // Not one another owner's turn has started already.
            }

            if (make.Count == 0)
                _ = toMake.Remove(owner);

            if (next != null)
            {
                this.Start(next);
                capacity--;
            }
        }
    }

    private bool TryRead(Entry entry)
    {
        if (this.disk == null || !this.disk.Ready || entry.Reading || entry.Generating || !this.disk.Contains(entry.Key))
            return false;

        TerrainTileKey key = entry.Key;
        if (!this.disk.TryRead(key, data => this.reads.Enqueue((key, data))))
            return false;

        entry.Reading = true;
        return true;
    }

    private int FreeWorkers() => this.settings.SampleOnMainThread
        ? (this.mainThreadJob == null && this.running < 2 ? 1 : 0)
        : this.workers.Count - this.running;

    private void StartJobs(long now)
    {
        // Until the disk index is built nothing is known to be cached, so nothing is sampled either.
        if (this.DiskIndexing)
            return;

        // Broken: nothing is wanted, but the sampler is checked again once a minute, on the column that failed it.
        if (this.FailureReason != null)
        {
            if (now >= this.nextCheck)
            {
                this.nextCheck = now + RetryMilliseconds;
                if (this.CheckSampler() == null)
                    this.Recover();
            }

            return;
        }

        // Tiles nobody has made are made ahead of any owner's room only onto a disk, where they wait for it at no cost in
        // memory. Without one, Feed makes them as owners have room.
        if (!this.DiskReadable)
            return;

        int capacity = this.FreeWorkers();
        while (capacity-- > 0 && this.TryPickCold(out Entry? entry))
            this.Start(entry!);
    }

    private void Start(Entry entry)
    {
        entry.Generating = true;
        TerrainTileJob job = new(entry.Key);
        this.running++;
        if (this.settings.SampleOnMainThread)
            this.mainThreadJob = job;
        else
            this.workers.Submit(job);
    }

    // Round robin between owners, each owner's most urgent tile that nobody has made yet.
    private bool TryPickCold(out Entry? picked)
    {
        picked = null;
        for (int visited = 0; visited < this.ownerOrder.Count; visited++)
        {
            this.ownerCursor %= this.ownerOrder.Count;
            string owner = this.ownerOrder[this.ownerCursor++];
            int best = int.MaxValue;
            foreach (TerrainTileKey key in this.wanted[owner])
            {
                Entry entry = this.entries[key];
                if (entry.Data != null || entry.Reading || entry.Generating || entry.Evicted || (this.disk?.Contains(key) ?? false))
                    continue;

                int priority = entry.Wanted[owner];
                if (picked == null || priority < best)
                {
                    picked = entry;
                    best = priority;
                }
            }

            if (picked != null)
                return true;
        }

        return false;
    }

    private void RunJob(TerrainTileJob job, CancellationToken token)
    {
        try
        {
            if (!job.Sampled)
                job.Sample(this.sample, this.mapSizeX, this.mapSizeZ, double.PositiveInfinity, () => this.throttle, token);

            byte[] encoded = TerrainTileCodec.Encode(job.Tile);
            this.disk?.Store(job.Key, encoded);
            job.Result = encoded;
        }
        catch (Exception ex)
        {
            job.Error = ex;
        }

        this.finished.Enqueue(job);
    }

    private void Keep(Entry entry, byte[] data)
    {
        if (entry.Data == null)
            this.memoryBytes += data.Length;

        entry.Data = data;
        entry.Evicted = false;
        entry.Used = ++this.serial;
    }

    // Hands the tile, just made or read, to every owner that wants it and has room. The others go on wanting it.
    private void Deliver(Entry entry)
    {
        entry.Used = ++this.serial;
        foreach (string owner in entry.Wanted.Keys.ToArray())
        {
            if (this.RoomFor(owner) > 0)
                this.HandOver(owner, entry);
        }
    }

    private void HandOver(string owner, Entry entry)
    {
        if (this.wanted.TryGetValue(owner, out HashSet<TerrainTileKey>? keys))
            _ = keys.Remove(entry.Key);

        _ = entry.Wanted.Remove(owner);
        this.ready(owner, entry.Key, entry.Data!);
    }

    private void Refuse(string owner, TerrainTileKey key, string reason)
    {
        this.Refusals++;
        this.refused(owner, key, reason);
    }

    // An entry with nothing to keep it goes.
    private void Forget(Entry entry)
    {
        if (entry.Wanted.Count == 0 && entry.Data == null && !entry.Reading && !entry.Generating)
            _ = this.entries.Remove(entry.Key);
    }

    // Memory holds no more than its budget: tiles nobody wants leave first, then tiles owners without room are waiting
    // for, least recently used first. Those are marked, so they are fetched again, from disk or by making them again,
    // only when an owner has room, never just to be dropped again.
    private void Trim()
    {
        if (this.memoryBytes <= this.memoryLimit)
            return;

        foreach (Entry entry in this.entries.Values.Where(e => e.Data != null).OrderBy(e => e.Wanted.Count > 0).ThenBy(e => e.Used).ToArray())
        {
            if (this.memoryBytes <= this.memoryLimit)
                break;

            this.memoryBytes -= entry.Data!.Length;
            entry.Data = null;
            entry.Evicted = entry.Wanted.Count > 0;
            this.Forget(entry);
        }
    }

    // The nearer of the limit for the step and the limit for every step.
    private long DistanceLimit(TerrainTileKey key) => Math.Min((long)this.settings.MaxDistancePerStep * key.Step, this.settings.MaxDistance);

    private bool TooFar(TerrainTileKey key, TerrainTileOwner info)
    {
        double limit = this.DistanceLimit(key);
        return key.DistanceSquaredTo(info.X, info.Z) > limit * limit;
    }

    private string TooFarReason(TerrainTileKey key)
        => string.Create(CultureInfo.InvariantCulture, $"Tiles at step {key.Step} are only served when their centre is within {this.DistanceLimit(key)} blocks of the player.");

    public string Describe()
    {
        StringBuilder text = new();
        _ = text.Append(CultureInfo.InvariantCulture, $"Tiles made {this.Generated}");
        if (this.Generated > 0)
            _ = text.Append(CultureInfo.InvariantCulture, $" ({this.GenerateMilliseconds / this.Generated:0} ms each to sample, {this.GeneratedBytes / 1024.0 / this.Generated:0.0} KiB each)");

        _ = text.Append(CultureInfo.InvariantCulture, $", from memory {this.MemoryHits}, from disk {this.DiskHits}, failed {this.Failures}, refused {this.Refusals}.\n");
        if (this.FailureReason != null)
            _ = text.Append(CultureInfo.InvariantCulture, $"Off: {this.FailureReason}\n");

        _ = text.Append(CultureInfo.InvariantCulture, $"Now: {this.running} being made on {this.workers.Count} worker(s), {this.Waiting} waiting, {this.Owners} owner(s) wanting tiles.\n");
        _ = text.Append(CultureInfo.InvariantCulture, $"Memory: {this.MemoryTiles} tiles, {this.memoryBytes / (1024.0 * 1024.0):0.0} of {this.memoryLimit / (1024 * 1024)} MiB.");
        if (this.disk?.Disabled == true)
            _ = text.Append("\nDisk: off after an error.");
        else if (this.disk != null)
            _ = text.Append(CultureInfo.InvariantCulture, $"\nDisk: {this.disk.Count} tiles, {this.disk.Bytes / (1024.0 * 1024.0):0.0} MiB{(this.disk.Ready ? "" : ", still indexing")}.");

        return text.ToString();
    }

    /// <summary>Stops the workers and waits for them, then closes the disk cache. Call before the sampler disposes its caches.</summary>
    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.workers.Dispose();
        this.disk?.Dispose();
        this.entries.Clear();
        this.wanted.Clear();
        this.ownerOrder.Clear();
    }

    private sealed class Entry
    {
        public readonly TerrainTileKey Key;
        public readonly Dictionary<string, int> Wanted = new(StringComparer.Ordinal);
        public byte[]? Data;
        public long Used;
        public bool Reading;
        public bool Generating;

        /// <summary>Made, then dropped from memory while still wanted: fetched again only for an owner with room.</summary>
        public bool Evicted;

        public Entry(TerrainTileKey key) => this.Key = key;
    }
}
