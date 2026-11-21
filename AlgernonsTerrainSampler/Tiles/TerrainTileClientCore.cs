#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>
/// A client's tiles: from a small memory cache of encoded tiles, then the client's own disk cache under the server's
/// identity, then the server. Tiles arrive in fragments, are checked and decoded off the main thread, and are handed to
/// every consumer that asked through <see cref="Requests"/>.
/// </summary>
/// <remarks>
/// <para>The server takes no more than its <see cref="TerrainTileStatus.MaxWantedTiles"/> from one client, so the client
/// keeps no more than that on the server's books: tiles it has asked for and not yet had whole or been refused. The
/// rest wait here, most urgent first, until room frees up, rather than coming back refused.</para>
/// <para>Main thread only, apart from decoding and disk writes, which run on the thread pool. It knows nothing of the
/// game: the mod system feeds it the server's messages and sends what it asks to send.</para>
/// </remarks>
internal sealed class TerrainTileClientCore : ITerrainTileSource, IDisposable
{
    private const long RecentLimit = 32L * 1024 * 1024;
    private const long HelloMilliseconds = 5000;
    private const long NoServerMilliseconds = 10000;
    private const int DefaultMaxWanted = 1024;
    private const int MaxAssemblies = 4;
    private const string NoServerReason = "This server does not serve terrain tiles: it does not run Algernon's Terrain Sampler 2.0.0 or later.";

    private static readonly Comparer<(int Priority, long Serial, TerrainTileKey Key)> UrgentFirst
        = Comparer<(int Priority, long Serial, TerrainTileKey Key)>.Create((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Serial.CompareTo(b.Serial));

    private readonly Action<TerrainTileWant> send;
    private readonly Action? sendHello;
    private readonly Action<TerrainTileReceived>? sendReceived;
    private readonly Func<string, TerrainTileDiskCache?> openDisk;
    private readonly Action<string>? log;
    private readonly Action<Action> background;
    private readonly Dictionary<TerrainTileKey, Pending> pending = [];
    private readonly Dictionary<TerrainTileKey, Assembly> assemblies = [];

    // Tiles for the server not yet asked for, most urgent first; adds to send (new, or sooner); drops to send.
    private readonly SortedSet<(int Priority, long Serial, TerrainTileKey Key)> held = new(UrgentFirst);
    private readonly Dictionary<TerrainTileKey, int> adds = [];
    private readonly HashSet<TerrainTileKey> drops = [];
    private int onServer;
    private readonly Dictionary<TerrainTileKey, (byte[] Data, long Used)> recent = [];
    private readonly ConcurrentQueue<(TerrainTileKey Key, byte[]? Data)> diskReads = new();
    private readonly ConcurrentQueue<Decoded> decoded = new();
    private TerrainTileDiskCache? disk;
    private string cacheIdentity = "";
    private long recentBytes;
    private long serial;
    private long firstTick = -1;
    private long nextHello;
    private bool wasConnected;
    private bool disposed;

    public TerrainTileClientCore(
        Action<TerrainTileWant> send,
        Func<string, TerrainTileDiskCache?> openDisk,
        Action<string>? log = null,
        Action? sendHello = null,
        Action<TerrainTileReceived>? sendReceived = null,
        Action<Action>? background = null)
    {
        this.send = send;
        this.sendHello = sendHello;
        this.sendReceived = sendReceived;
        this.openDisk = openDisk;
        this.log = log;
        this.background = background ?? (work => _ = Task.Run(work));
        this.Requests = new TerrainTileRequests(this, log);
    }

    /// <summary>Raised on the main thread whenever the server answers a hello, whatever it says.</summary>
    public event Action? StatusReceived;

    public TerrainTileRequests Requests { get; }

    public bool Available { get; private set; }

    public string UnavailableReason { get; private set; } = "Waiting for the server.";

    public string Identity { get; private set; } = "";

    public TerrainTileStatus? Status { get; private set; }

    public TerrainTileProgress? LastProgress { get; private set; }

    public long Received { get; private set; }

    public long ReceivedBytes { get; private set; }

    public long FromDisk { get; private set; }

    public long FromMemory { get; private set; }

    public long Refused { get; private set; }

    public int InFlight => this.pending.Count;

    /// <summary>Tiles asked of the server and not yet answered.</summary>
    public int OnServer => this.onServer;

    /// <summary>Tiles for the server waiting for room on its books.</summary>
    public int Held => this.held.Count;

    /// <summary>Tiles part of which has arrived.</summary>
    public int Assembling => this.assemblies.Count;

    private int MaxWanted => this.Status?.MaxWantedTiles > 0 ? this.Status.MaxWantedTiles : DefaultMaxWanted;

    public TerrainTileDiskCache? Disk => this.disk;

    /// <summary>Raised on the main thread when <see cref="Available"/> changes.</summary>
    public event Action? AvailabilityChanged;

    public string? Want(TerrainTileKey key, int priority)
    {
        // Told at the next tick, with the reason as it is now, to the requests standing now.
        if (!this.Available)
            return this.UnavailableReason;

        if (this.pending.TryGetValue(key, out Pending? existing))
        {
            if (existing.Stage == Stage.Server && priority < existing.Priority)
            {
                if (existing.Sent)
                {
                    // Sooner: the server's add changes its priority, and takes no more room. Not worth sending for a tile
                    // whose pieces are already arriving.
                    existing.Priority = priority;
                    if (!this.assemblies.ContainsKey(key))
                        this.adds[key] = priority;
                }
                else
                {
                    _ = this.held.Remove(HeldEntry(key, existing));
                    existing.Priority = priority;
                    _ = this.held.Add(HeldEntry(key, existing));
                }
            }

            return null;
        }

        Pending entry = new() { Priority = priority };
        this.pending[key] = entry;
        if (this.recent.TryGetValue(key, out (byte[] Data, long Used) kept))
        {
            this.FromMemory++;
            this.recent[key] = (kept.Data, ++this.serial);
            entry.Stage = Stage.Decoding;
            this.DecodeLater(key, kept.Data, store: false, entry);
        }
        else if (this.disk != null && this.disk.Contains(key) && this.disk.TryRead(key, data => this.diskReads.Enqueue((key, data))))
        {
            entry.Stage = Stage.Disk;
        }
        else
        {
            this.AskServer(key, entry);
        }

        return null;
    }

    public void Drop(TerrainTileKey key)
    {
        if (this.pending.Remove(key, out Pending? entry))
            this.Withdraw(key, entry);
    }

    // The entry no longer waits on the server: it leaves the held tiles, or the server's books, and the server is told to
    // forget a tile it was asked for. An entry at any other stage has nothing on the server.
    private void Withdraw(TerrainTileKey key, Pending entry)
    {
        if (entry.Stage != Stage.Server)
            return;

        if (entry.Sent)
        {
            this.onServer--;
            _ = this.adds.Remove(key);
            _ = this.drops.Add(key);
        }
        else
        {
            _ = this.held.Remove(HeldEntry(key, entry));
        }

        entry.Sent = false;
    }

    // Held until the server's books have room. A drop of the same tile not yet sent still goes, before the add: the
    // server may be sending the tile already, and this client may have let its first pieces go, so the server must know
    // to send it again rather than take the add for a change of priority.
    private void AskServer(TerrainTileKey key, Pending entry)
    {
        entry.Stage = Stage.Server;
        entry.Sent = false;
        entry.Serial = ++this.serial;
        _ = this.held.Add(HeldEntry(key, entry));
    }

    // The server has answered a tile it was asked for, or has started to: it is off its books.
    private void Answered(TerrainTileKey key, Pending entry)
    {
        if (entry.Sent)
            this.onServer--;
        else
            _ = this.held.Remove(HeldEntry(key, entry));

        entry.Sent = false;
    }

    private static (int Priority, long Serial, TerrainTileKey Key) HeldEntry(TerrainTileKey key, Pending entry) => (entry.Priority, entry.Serial, key);

    public void OnStatus(TerrainTileStatus status)
    {
        this.Status = status;
        bool available = status.Protocol == TerrainTileProtocol.Version && status.Available && TerrainTileIdentity.IsValid(status.Identity);
        string reason = status.Protocol != TerrainTileProtocol.Version
            ? $"The server speaks tile protocol {status.Protocol}; this client speaks {TerrainTileProtocol.Version}. Update the terrain sampler on both."
            : status.Available ? "" : status.Reason;

        // The caches belong to an identity, so a server that comes back with the same one, as after its sampler was
        // broken for a while, keeps them.
        if (available && this.cacheIdentity != status.Identity)
        {
            this.disk?.Dispose();
            this.disk = this.openDisk(status.Identity);
            this.cacheIdentity = status.Identity;
            this.recent.Clear();
            this.recentBytes = 0;
        }

        this.Identity = available ? status.Identity : "";
        this.SetAvailable(available, reason);
        this.StatusReceived?.Invoke();
    }

    public void OnDisconnected(string reason) => this.SetAvailable(false, reason);

    /// <summary>Why tiles are unavailable while there is no answer from the server, for the refusals until there is.</summary>
    public void SetWaitingReason(string reason)
    {
        if (!this.Available)
            this.UnavailableReason = reason;
    }

    private void SetAvailable(bool available, string reason)
    {
        bool changed = available != this.Available;
        this.Available = available;
        this.UnavailableReason = available ? "" : reason;
        if (!available)
        {
            this.pending.Clear();
            this.assemblies.Clear();
            this.held.Clear();
            this.adds.Clear();
            this.drops.Clear();
            this.onServer = 0;
            this.Requests.FailAll(reason);
        }

        if (changed)
            this.AvailabilityChanged?.Invoke();
    }

    public void OnData(TerrainTileData data)
    {
        TerrainTileKey key = new(data.Step, data.TileX, data.TileZ);
        byte[]? piece = data.Data;
        if (piece == null || data.Total <= 0 || data.Total > TerrainTileProtocol.MaxTileBytes || data.Offset < 0 || data.Offset + piece.Length > data.Total)
            return;

        // Every tile whose last piece arrives is acknowledged, kept or not: the server sends no more until it hears.
        if (data.Offset + piece.Length == data.Total)
            this.sendReceived?.Invoke(new TerrainTileReceived { Step = data.Step, TileX = data.TileX, TileZ = data.TileZ });

        if (data.Offset == 0)
        {
            // Only a tile this client is waiting on from the server is put together; a tile already started is
            // finished even if it is dropped meanwhile, since most of it has crossed the network.
            if (!this.pending.TryGetValue(key, out Pending? asked) || asked.Stage != Stage.Server)
            {
                _ = this.assemblies.Remove(key);
                return;
            }

            // The server sends a client one tile at a time, so more than a few part-assembled means pieces were lost.
            if (!this.assemblies.ContainsKey(key) && this.assemblies.Count >= MaxAssemblies)
                _ = this.assemblies.Remove(this.assemblies.MinBy(pair => pair.Value.Started).Key);

            this.assemblies[key] = new Assembly(new byte[data.Total], ++this.serial);
        }

        if (!this.assemblies.TryGetValue(key, out Assembly? assembly) || assembly.Buffer.Length != data.Total || assembly.Received != data.Offset)
        {
            _ = this.assemblies.Remove(key); // Out of order: wait for the tile to start again.
            return;
        }

        piece.CopyTo(assembly.Buffer, data.Offset);
        assembly.Received += piece.Length;
        if (assembly.Received < data.Total)
            return;

        _ = this.assemblies.Remove(key);
        this.Received++;
        this.ReceivedBytes += data.Total;

        // A tile nobody wants any more is still worth keeping: it crossed the network already.
        Pending? waiting = null;
        if (this.pending.TryGetValue(key, out Pending? entry) && entry.Stage == Stage.Server)
        {
            this.Answered(key, entry);
            entry.Stage = Stage.Decoding;
            waiting = entry;
        }

        this.DecodeLater(key, assembly.Buffer, store: true, waiting);
    }

    public void OnRefused(TerrainTileRefused refused)
    {
        foreach (TerrainTileKey key in refused.Keys())
        {
            // Only a tile the server was asked for: one still held here has not been, so a refusal for it is stale.
            this.Refused++;
            if (this.pending.TryGetValue(key, out Pending? entry) && entry.Stage == Stage.Server && entry.Sent)
            {
                this.Answered(key, entry);
                _ = this.pending.Remove(key);
                this.Requests.Fail(key, refused.Reason);
            }
        }
    }

    public void OnProgress(TerrainTileProgress progress) => this.LastProgress = progress;

    /// <summary>
    /// A tick with the channel's state: says hello as soon as the channel connects, and again every five seconds until
    /// the server answers; says why when the channel never connects; refuses everything when it drops.
    /// </summary>
    public void Tick(bool connected, long now)
    {
        if (this.firstTick < 0)
            this.firstTick = now;

        if (connected)
        {
            this.wasConnected = true;
            if (this.Status == null && now >= this.nextHello)
            {
                this.nextHello = now + HelloMilliseconds;
                this.sendHello?.Invoke();
            }
        }
        else if (this.wasConnected)
        {
            this.wasConnected = false;
            this.Status = null;
            this.nextHello = 0;
            this.OnDisconnected("Disconnected from the server.");
        }
        else if (!this.Available && now - this.firstTick >= NoServerMilliseconds)
        {
            // A server with the sampler connects its channel while the world loads, so this long without it means none.
            this.SetWaitingReason(NoServerReason);
        }

        this.Tick();
    }

    public void Tick()
    {
        if (this.disposed)
            return;

        this.Requests.Tick();

        while (this.diskReads.TryDequeue(out (TerrainTileKey Key, byte[]? Data) read))
        {
            if (!this.pending.TryGetValue(read.Key, out Pending? entry) || entry.Stage != Stage.Disk)
                continue;

            if (read.Data != null)
            {
                this.FromDisk++;
                entry.Stage = Stage.Decoding;
                this.DecodeLater(read.Key, read.Data, store: false, entry);
            }
            else
            {
                this.AskServer(read.Key, entry); // Damaged on disk, and now gone from it.
            }
        }

        while (this.decoded.TryDequeue(out Decoded? result))
        {
            if (result.Data != null)
                this.Remember(result.Key, result.Data);

            // A consumer may have cancelled the tile and asked for it again while it decoded, so what is pending for the
            // key may be a newer entry, held here or asked of the server. A sound tile answers it all the same, and the
            // entry leaves whatever it waited on, the server's books included; a damaged one answers only its own.
            if (!this.pending.TryGetValue(result.Key, out Pending? entry) || (result.Tile == null && entry != result.For))
                continue;

            this.Withdraw(result.Key, entry);
            _ = this.pending.Remove(result.Key);
            if (result.Tile != null)
                this.Requests.Complete(result.Tile);
            else
                this.Requests.Fail(result.Key, result.Error ?? "The tile could not be read.");
        }

        this.SendWants();
    }

    private void SendWants()
    {
        if (!this.Available)
            return;

        // The most urgent held tiles, as many as the server's books have room for.
        while (this.onServer < this.MaxWanted && this.held.Count > 0)
        {
            (int priority, _, TerrainTileKey key) = this.held.Min;
            _ = this.held.Remove(this.held.Min);
            this.pending[key].Sent = true;
            this.onServer++;
            this.adds[key] = priority;
        }

        if (this.adds.Count == 0 && this.drops.Count == 0)
            return;

        // Most urgent first, so the server starts on them first even before priorities are compared.
        KeyValuePair<TerrainTileKey, int>[] added = [.. this.adds.OrderBy(pair => pair.Value)];
        TerrainTileKey[] dropped = [.. this.drops];
        this.adds.Clear();
        this.drops.Clear();

        // Every drop before any add, so the server has made the room the adds were counted against.
        int dropAt = 0, addAt = 0;
        while (dropAt < dropped.Length || addAt < added.Length)
        {
            TerrainTileKey[] dropping = [.. dropped.Skip(dropAt).Take(TerrainTileProtocol.MaxKeysPerWant)];
            dropAt += dropping.Length;
            KeyValuePair<TerrainTileKey, int>[] adding = dropAt < dropped.Length ? [] : [.. added.Skip(addAt).Take(TerrainTileProtocol.MaxKeysPerWant)];
            addAt += adding.Length;
            this.send(new TerrainTileWant
            {
                AddStep = [.. adding.Select(p => p.Key.Step)],
                AddX = [.. adding.Select(p => p.Key.TileX)],
                AddZ = [.. adding.Select(p => p.Key.TileZ)],
                AddPriority = [.. adding.Select(p => p.Value)],
                DropStep = [.. dropping.Select(k => k.Step)],
                DropX = [.. dropping.Select(k => k.TileX)],
                DropZ = [.. dropping.Select(k => k.TileZ)]
            });
        }
    }

    // Decodes off the main thread, for the entry that is waiting on it (null for a tile nobody waits on any more).
    private void DecodeLater(TerrainTileKey key, byte[] data, bool store, Pending? entry)
    {
        TerrainTileDiskCache? cache = store ? this.disk : null;
        this.background(() =>
        {
            try
            {
                TerrainTile tile = TerrainTileCodec.Decode(data, key);
                cache?.Store(key, data);
                this.decoded.Enqueue(new Decoded(key, entry, tile, null, data));
            }
            catch (Exception ex)
            {
                this.log?.Invoke($"tile {key} from the server could not be read: {ex.Message}");
                this.decoded.Enqueue(new Decoded(key, entry, null, $"The tile arrived damaged: {ex.Message}", null));
            }
        });
    }

    private void Remember(TerrainTileKey key, byte[] data)
    {
        if (this.recent.Remove(key, out (byte[] Data, long Used) previous))
            this.recentBytes -= previous.Data.Length;

        this.recent[key] = (data, ++this.serial);
        this.recentBytes += data.Length;
        if (this.recentBytes <= RecentLimit)
            return;

        foreach (KeyValuePair<TerrainTileKey, (byte[] Data, long Used)> old in this.recent.OrderBy(pair => pair.Value.Used).ToArray())
        {
            if (this.recentBytes <= RecentLimit)
                break;

            _ = this.recent.Remove(old.Key);
            this.recentBytes -= old.Value.Data.Length;
        }
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.disk?.Dispose();
        this.disk = null;
    }

    private enum Stage
    {
        Disk,
        Server,
        Decoding
    }

    private sealed class Pending
    {
        public Stage Stage;
        public int Priority;

        /// <summary>At <see cref="Stage.Server"/>: asked of the server and on its books, rather than held here.</summary>
        public bool Sent;

        /// <summary>Orders held tiles of one priority, oldest first.</summary>
        public long Serial;
    }

    private sealed class Assembly
    {
        public readonly byte[] Buffer;
        public readonly long Started;
        public int Received;

        public Assembly(byte[] buffer, long started)
        {
            this.Buffer = buffer;
            this.Started = started;
        }
    }

    private sealed record Decoded(TerrainTileKey Key, Pending? For, TerrainTile? Tile, string? Error, byte[]? Data);
}
