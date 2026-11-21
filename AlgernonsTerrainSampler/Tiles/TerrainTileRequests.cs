#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>Where a side's tiles come from: the service on the server, the channel and its caches on a client.</summary>
internal interface ITerrainTileSource
{
    /// <summary>
    /// Wants the tile, or wants it sooner; answered later with <see cref="TerrainTileRequests.Complete"/> or
    /// <see cref="TerrainTileRequests.Fail"/>. Returns null, or, when the source cannot ask at all now, why not: the
    /// requests standing for the tile then are refused at the next <see cref="TerrainTileRequests.Tick"/>.
    /// </summary>
    string? Want(TerrainTileKey key, int priority);

    /// <summary>Nobody on this side wants the tile any more.</summary>
    void Drop(TerrainTileKey key);
}

/// <summary>
/// The server's own consumers' source: the service, under an owner no player can have, or, before it has started and
/// after it has stopped, a refusal with the reason. Every answer waits for <see cref="Tick"/>, so a consumer never hears
/// back inside its own request, and one that cancels first never hears at all.
/// </summary>
/// <remarks>Main thread only, apart from decoding, which runs on the thread pool.</remarks>
internal sealed class TerrainTileLocalSource : ITerrainTileSource
{
    /// <summary>Not a possible player UID, so mods on the server never share an owner with a player.</summary>
    public const string Owner = "\u0001server";

    private readonly Func<string> unavailableReason;
    private readonly ConcurrentQueue<TerrainTile> decoded = new();
    private readonly ConcurrentQueue<(TerrainTileKey Key, string Reason)> unreadable = new();

    public TerrainTileLocalSource(Func<string> unavailableReason, Action<string>? log = null)
    {
        this.unavailableReason = unavailableReason;
        this.Requests = new TerrainTileRequests(this, log);
    }

    /// <summary>The server's consumers' requests, answered from this source.</summary>
    public TerrainTileRequests Requests { get; }

    /// <summary>The service while it runs; null before then and after.</summary>
    public TerrainTileService? Service { get; set; }

    public string? Want(TerrainTileKey key, int priority)
    {
        if (this.Service == null)
            return this.unavailableReason();

        this.Service.Want(Owner, key, priority);
        return null;
    }

    public void Drop(TerrainTileKey key) => this.Service?.Drop(Owner, key);

    /// <summary>The service's tile: decoded off the main thread, handed over at a later tick.</summary>
    public void Ready(TerrainTileKey key, byte[] data)
    {
        _ = Task.Run(() =>
        {
            try
            {
                this.decoded.Enqueue(TerrainTileCodec.Decode(data, key));
            }
            catch (Exception ex)
            {
                this.unreadable.Enqueue((key, $"The tile could not be read: {ex.Message}"));
            }
        });
    }

    /// <summary>
    /// The service's refusal, on the main thread: told at the next tick to the requests standing now, not to any made
    /// after it, which the service is asked about afresh.
    /// </summary>
    public void Refused(TerrainTileKey key, string reason) => this.Requests.FailLater(key, reason);

    /// <summary>Hands every answer so far to the requests; those cancelled meanwhile are not told.</summary>
    public void Tick()
    {
        this.Requests.Tick();
        while (this.unreadable.TryDequeue(out (TerrainTileKey Key, string Reason) refusal))
            this.Requests.Fail(refusal.Key, refusal.Reason);

        while (this.decoded.TryDequeue(out TerrainTile? tile))
            this.Requests.Complete(tile);
    }
}

/// <summary>
/// Every consumer mod's requests on one side, gathered by tile: however many mods ask for a tile, the source is asked
/// once, at the most urgent of their priorities, and every one of them gets the same tile back.
/// </summary>
/// <remarks>
/// <para>A refusal decided before it can be told, because no callback may run inside a request, belongs to the requests
/// standing when it was decided (<see cref="FailLater"/>). A request made meanwhile, as by a consumer that asks again
/// when the service becomes available, is asked of the source afresh and never hears the older refusal.</para>
/// <para>Main thread only, as are the callbacks it makes.</para>
/// </remarks>
internal sealed class TerrainTileRequests
{
    private readonly ITerrainTileSource source;
    private readonly Action<string>? log;
    private readonly Dictionary<long, Entry> requests = [];
    private readonly Dictionary<TerrainTileKey, HashSet<long>> byKey = [];
    private readonly Dictionary<TerrainTileKey, int> asked = [];
    private readonly List<(TerrainTileKey Key, long[] Handles, string Reason)> refusals = [];
    private long nextHandle;

    public TerrainTileRequests(ITerrainTileSource source, Action<string>? log = null)
    {
        this.source = source;
        this.log = log;
    }

    public int Pending => this.requests.Count;

    public long Request(TerrainTileKey key, int priority, Action<TerrainTile> ready, Action<string>? refused)
    {
        long handle = ++this.nextHandle;
        this.requests[handle] = new Entry(key, priority, ready, refused);
        if (!this.byKey.TryGetValue(key, out HashSet<long>? handles))
        {
            handles = [];
            this.byKey[key] = handles;
        }

        _ = handles.Add(handle);
        if (!this.asked.TryGetValue(key, out int current) || priority < current)
        {
            this.asked[key] = priority;
            if (this.source.Want(key, priority) is string cannot)
                this.FailLater(key, cannot);
        }

        return handle;
    }

    /// <summary>
    /// Refuses, at the next <see cref="Tick"/>, the requests standing for the tile now, those cancelled meanwhile
    /// excepted. The tile counts as not asked of the source from now on, so a request made before then asks again.
    /// </summary>
    public void FailLater(TerrainTileKey key, string reason)
    {
        _ = this.asked.Remove(key);
        if (this.byKey.TryGetValue(key, out HashSet<long>? handles))
            this.refusals.Add((key, [.. handles], reason));
    }

    /// <summary>Tells the refusals decided since the last tick.</summary>
    public void Tick()
    {
        if (this.refusals.Count == 0)
            return;

        (TerrainTileKey Key, long[] Handles, string Reason)[] due = [.. this.refusals];
        this.refusals.Clear();
        foreach ((TerrainTileKey key, long[] handles, string reason) in due)
        {
            foreach (long handle in handles)
            {
                if (!this.requests.Remove(handle, out Entry? request))
                    continue;

                HashSet<long> standing = this.byKey[key];
                _ = standing.Remove(handle);
                if (standing.Count == 0)
                {
                    _ = this.byKey.Remove(key);
                    _ = this.asked.Remove(key);
                }

                this.Refuse(request, reason);
            }
        }
    }

    public void Cancel(long handle)
    {
        if (!this.requests.Remove(handle, out Entry? request))
            return;

        HashSet<long> handles = this.byKey[request.Key];
        _ = handles.Remove(handle);
        if (handles.Count == 0)
        {
            _ = this.byKey.Remove(request.Key);
            _ = this.asked.Remove(request.Key);
            this.source.Drop(request.Key);
        }
    }

    public void Complete(TerrainTile tile)
    {
        foreach (Entry request in this.Take(tile.Key))
        {
            try
            {
                request.Ready(tile);
            }
            catch (Exception ex)
            {
                this.log?.Invoke($"a consumer's tile callback threw: {ex}");
            }
        }
    }

    public void Fail(TerrainTileKey key, string reason)
    {
        foreach (Entry request in this.Take(key))
            this.Refuse(request, reason);
    }

    private void Refuse(Entry request, string reason)
    {
        try
        {
            request.Refused?.Invoke(reason);
        }
        catch (Exception ex)
        {
            this.log?.Invoke($"a consumer's refusal callback threw: {ex}");
        }
    }

    /// <summary>Refuses everything outstanding, as when the service goes away.</summary>
    public void FailAll(string reason)
    {
        foreach (TerrainTileKey key in this.byKey.Keys.ToArray())
            this.Fail(key, reason);
    }

    private List<Entry> Take(TerrainTileKey key)
    {
        _ = this.asked.Remove(key);
        if (!this.byKey.Remove(key, out HashSet<long>? handles))
            return [];

        List<Entry> taken = new(handles.Count);
        foreach (long handle in handles)
        {
            if (this.requests.Remove(handle, out Entry? request))
                taken.Add(request);
        }

        return taken;
    }

    private sealed record Entry(TerrainTileKey Key, int Priority, Action<TerrainTile> Ready, Action<string>? Refused);
}
