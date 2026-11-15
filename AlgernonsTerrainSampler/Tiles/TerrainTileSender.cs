#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>
/// The server's end of the channel, per client: tiles in fragments of at most <see cref="TerrainTileProtocol.FragmentBytes"/>,
/// and refusals, under a byte budget per tick shared by every client, round robin so that one client's burst cannot
/// starve the rest. A client gets one tile finished before the next is started, and no more than
/// <see cref="TerrainTileProtocol.WindowTiles"/> it has not yet said it received.
/// </summary>
/// <remarks>
/// <para>The window is there because the game's send does not wait: it hands each packet to the socket and has no queue
/// limit, so without it a slow client's backlog would sit in server memory with its gameplay packets behind it. It binds
/// a client that acknowledges honestly. One that acknowledges tiles it has not read can still have the game buffer
/// sends to it at up to the byte budget's rate, and the game's <c>SendAsync</c> offers neither a limit nor a way to see
/// how much is buffered; the budget per tick, shared by every client, is what bounds that.</para>
/// <para>The game's channel compresses every packet over 1,460 bytes with Zstandard while the server's
/// <c>CompressPackets</c> is on, as it is by default, and <c>IServerNetworkChannel</c> has no way to send one without.
/// Tiles are Brotli already, so that is some CPU for nothing; Zstandard passes data it cannot compress quickly, and
/// fragments small enough to escape it would cost more in packets than it does.</para>
/// <para>A client's queue holds each tile once, and no more than <see cref="QueueTiles"/>: the service hands a client a
/// tile only while there is room (<see cref="Room"/>), and keeps the rest wanted, within its own memory budget, until
/// there is. So the sender holds the bytes of three tiles a client at most, the queued ones and the one being sent,
/// whatever the client does; tiles sent and not yet acknowledged it holds by key alone. A tile leaves the queue when the
/// client stops wanting it before it has started, and the service counts the queue against the client's
/// <see cref="TerrainTileService.Settings.MaxWantedTiles"/>.</para>
/// <para>Refusals wait for the client's turn too, gathered by reason, so one message answers thousands of tiles. A
/// client never has more than <see cref="MaxQueuedRefusals"/> waiting: a well behaved one cannot, since each tile it adds
/// is answered once and it adds no more than the limit, so past that the rest are dropped.</para>
/// <para>Main thread only.</para>
/// </remarks>
internal sealed class TerrainTileSender : ITerrainTileQueue
{
    public const int MaxQueuedRefusals = 65536;

    /// <summary>Tiles a client may have queued behind the one being sent: enough that the window never waits on the service.</summary>
    public const int QueueTiles = 2;

    private const int ProgressMilliseconds = 5000;

    private readonly Action<string, TerrainTileData> sendData;
    private readonly Action<string, TerrainTileRefused> sendRefused;
    private readonly Action<string, TerrainTileProgress> sendProgress;
    private readonly Action<string>? log;
    private readonly Dictionary<string, Client> clients = new(StringComparer.Ordinal);
    private readonly List<string> order = [];
    private int cursor;

    public TerrainTileSender(
        Action<string, TerrainTileData> sendData,
        Action<string, TerrainTileRefused> sendRefused,
        Action<string, TerrainTileProgress> sendProgress,
        Action<string>? log = null)
    {
        this.sendData = sendData;
        this.sendRefused = sendRefused;
        this.sendProgress = sendProgress;
        this.log = log;
    }

    public long BytesSent { get; private set; }

    public long TilesSent { get; private set; }

    public long TilesRefused { get; private set; }

    public long RefusalsSent { get; private set; }

    public int Clients => this.clients.Count;

    /// <summary>Refused tiles waiting to be told to the client.</summary>
    public int RefusalsQueued(string uid) => this.clients.TryGetValue(uid, out Client? client) ? client.RefusalCount : 0;

    /// <summary>Tiles on their way to the client: queued, being sent, and sent but not yet acknowledged.</summary>
    public int Sending(string uid)
        => this.clients.TryGetValue(uid, out Client? client) ? client.Queue.Count + (client.Current != null ? 1 : 0) + client.Unacknowledged.Count : 0;

    /// <summary>The client has the whole tile: the window has room for another. Keys it was not sent are ignored.</summary>
    public void OnReceived(string uid, TerrainTileReceived received)
    {
        TerrainTileKey key = new(received.Step, received.TileX, received.TileZ);
        if (this.clients.TryGetValue(uid, out Client? client) && client.Unacknowledged.Remove(key)
            && client.Current?.Key != key && !client.Unacknowledged.Contains(key))
        {
            _ = client.DroppedInFlight.Remove(key);
        }
    }

    /// <summary>Queues a tile for the client, unless it is queued already.</summary>
    public void Enqueue(string uid, TerrainTileKey key, byte[] data)
    {
        Client client = this.ClientFor(uid);
        if (!client.Queued.ContainsKey(key))
            client.Queued[key] = client.Queue.AddLast((key, data));
    }

    /// <summary>Tells the client, in its turn, that it will not get the tile. Refusing a tile twice for one reason tells it once.</summary>
    public void Refuse(string uid, TerrainTileKey key, string reason)
    {
        Client client = this.ClientFor(uid);
        if (client.RefusalCount >= MaxQueuedRefusals)
        {
            if (!client.RefusalsDropped)
                this.log?.Invoke($"client {uid} has {MaxQueuedRefusals} refused tiles waiting to be told; dropping the rest.");

            client.RefusalsDropped = true;
            return;
        }

        if (!client.RefusalsByReason.TryGetValue(reason, out Refusals? refusals))
        {
            refusals = new Refusals(reason);
            client.RefusalsByReason[reason] = refusals;
            client.Refusals.Enqueue(refusals);
        }

        if (refusals.Set.Add(key))
        {
            refusals.Keys.Add(key);
            client.RefusalCount++;
        }
    }

    /// <summary>A client's want message: to the service, or, while there is none, every tile it adds refused, in one answer.</summary>
    public void OnWant(string uid, TerrainTileWant want, TerrainTileService? service, string unavailableReason)
    {
        this.Touch(uid);
        if (service != null)
        {
            service.Apply(uid, want);
            return;
        }

        foreach ((TerrainTileKey key, _) in want.Adds())
            this.Refuse(uid, key, unavailableReason);
    }

    /// <summary>
    /// A client that says hello or wants tiles is known from then, so it hears how its tiles are getting on every five
    /// seconds while any wait, from five seconds after they started to, whether or not one is yet on its way.
    /// </summary>
    public void Touch(string uid) => _ = this.ClientFor(uid);

    public void Remove(string uid)
    {
        if (this.clients.Remove(uid))
            _ = this.order.Remove(uid);
    }

    /// <summary>Tiles queued for the client that have not started on their way.</summary>
    public int Count(string owner) => this.clients.TryGetValue(owner, out Client? client) ? client.Queue.Count : 0;

    /// <summary>How many more tiles the client can be handed now; unlimited for an owner that is not a client here.</summary>
    public int Room(string owner) => this.clients.TryGetValue(owner, out Client? client) ? Math.Max(0, QueueTiles - client.Queue.Count) : int.MaxValue;

    /// <summary>The bytes of tiles the sender holds for the client: those queued and the one being sent.</summary>
    public long BytesHeld(string uid)
        => this.clients.TryGetValue(uid, out Client? client) ? client.Queue.Sum(item => (long)item.Data.Length) + (client.Current?.Data.Length ?? 0) : 0;

    public bool OnItsWay(string owner, TerrainTileKey key)
    {
        if (!this.clients.TryGetValue(owner, out Client? client))
            return false;

        if (client.Queued.ContainsKey(key))
            return true;

        bool inFlight = client.Current?.Key == key || client.Unacknowledged.Contains(key);
        return inFlight && !client.DroppedInFlight.Contains(key);
    }

    public IReadOnlyList<TerrainTileKey> Keys(string owner)
        => this.clients.TryGetValue(owner, out Client? client) ? [.. client.Queue.Select(item => item.Key)] : [];

    /// <summary>
    /// Takes a tile that has not started on its way out of the client's queue. One already started is finished, but is
    /// marked: the client may have let its first pieces go, so if it asks again, the tile is sent again.
    /// </summary>
    public bool Withdraw(string owner, TerrainTileKey key)
    {
        if (!this.clients.TryGetValue(owner, out Client? client))
            return false;

        if (client.Current?.Key == key || client.Unacknowledged.Contains(key))
            _ = client.DroppedInFlight.Add(key);

        if (!client.Queued.Remove(key, out LinkedListNode<(TerrainTileKey Key, byte[] Data)>? node))
            return false;

        client.Queue.Remove(node);
        return true;
    }

    public void WithdrawAll(string owner)
    {
        if (!this.clients.TryGetValue(owner, out Client? client))
            return;

        client.Queue.Clear();
        client.Queued.Clear();
        client.DroppedInFlight.UnionWith(client.Unacknowledged);
        if (client.Current != null)
            _ = client.DroppedInFlight.Add(client.Current.Value.Key);
    }

    /// <summary>
    /// Sends up to <paramref name="budgetBytes"/> of refusals and tile data, a message at a time to each client in turn,
    /// refusals first, and any progress reports that are due.
    /// </summary>
    /// <param name="wanted">How many tiles a client still wants that are not yet on their way.</param>
    public void Tick(int budgetBytes, long now, Func<string, int> wanted, int making, int waiting)
    {
        int sent = 0;
        int idle = 0;
        while (this.order.Count > 0 && idle < this.order.Count && sent < budgetBytes)
        {
            this.cursor %= this.order.Count;
            Client client = this.clients[this.order[this.cursor]];
            if (client.Refusals.Count > 0)
            {
                idle = 0;
                sent += this.SendRefusals(client);
                this.cursor++;
                continue;
            }

            if (client.Current == null)
            {
                if (client.Unacknowledged.Count >= TerrainTileProtocol.WindowTiles || client.Queue.First is not LinkedListNode<(TerrainTileKey Key, byte[] Data)> next)
                {
                    idle++;
                    this.cursor++;
                    continue;
                }

                client.Queue.RemoveFirst();
                _ = client.Queued.Remove(next.Value.Key);
                _ = client.DroppedInFlight.Remove(next.Value.Key); // This copy was asked for since any copy was dropped.
                client.Current = next.Value;
            }

            idle = 0;
            (TerrainTileKey key, byte[] data) = client.Current.Value;
            int count = Math.Min(TerrainTileProtocol.FragmentBytes, data.Length - client.Offset);
            this.sendData(client.Uid, new TerrainTileData
            {
                Step = key.Step,
                TileX = key.TileX,
                TileZ = key.TileZ,
                Total = data.Length,
                Offset = client.Offset,
                Data = data.AsSpan(client.Offset, count).ToArray()
            });

            client.Offset += count;
            sent += count;
            this.BytesSent += count;
            if (client.Offset >= data.Length)
            {
                client.Current = null;
                client.Offset = 0;
                client.Unacknowledged.Add(key);
                this.TilesSent++;
            }

            this.cursor++;
        }

        foreach (string uid in this.order.ToArray())
        {
            Client client = this.clients[uid];
            int left = wanted(client.Uid);
            int sending = this.Sending(client.Uid);
            if ((left == 0 && sending == 0) || client.LastProgress == long.MinValue)
            {
                client.LastProgress = now; // Nothing waiting, or newly known: the next report is five seconds after this.
            }
            else if (now - client.LastProgress >= ProgressMilliseconds)
            {
                client.LastProgress = now;
                this.sendProgress(client.Uid, new TerrainTileProgress { Wanted = left, Sending = sending, Making = making, Waiting = waiting });
            }
        }
    }

    // One message: up to MaxKeysPerWant of the oldest reason's keys. Returns about what it costs on the wire.
    private int SendRefusals(Client client)
    {
        Refusals refusals = client.Refusals.Peek();
        int count = Math.Min(refusals.Keys.Count, TerrainTileProtocol.MaxKeysPerWant);
        List<TerrainTileKey> keys = refusals.Keys.GetRange(0, count);
        refusals.Keys.RemoveRange(0, count);
        client.RefusalCount -= count;
        if (refusals.Keys.Count == 0)
        {
            _ = client.Refusals.Dequeue();
            _ = client.RefusalsByReason.Remove(refusals.Reason);
        }
        else
        {
            refusals.Set.ExceptWith(keys);
        }

        if (client.RefusalCount == 0)
            client.RefusalsDropped = false;

        this.sendRefused(client.Uid, TerrainTileRefused.For(keys, refusals.Reason));
        this.TilesRefused += count;
        this.RefusalsSent++;
        return 16 + (refusals.Reason.Length * 3) + (count * 12);
    }

    private Client ClientFor(string uid)
    {
        if (!this.clients.TryGetValue(uid, out Client? client))
        {
            client = new Client(uid);
            this.clients[uid] = client;
            this.order.Add(uid);
        }

        return client;
    }

    private sealed class Client
    {
        public readonly string Uid;

        /// <summary>Tiles not yet started, oldest first, and the same by key.</summary>
        public readonly LinkedList<(TerrainTileKey Key, byte[] Data)> Queue = new();
        public readonly Dictionary<TerrainTileKey, LinkedListNode<(TerrainTileKey Key, byte[] Data)>> Queued = [];
        public (TerrainTileKey Key, byte[] Data)? Current;
        public int Offset;

        /// <summary>Sent whole, not yet acknowledged, oldest first. A key may be here twice if it was asked for again.</summary>
        public readonly List<TerrainTileKey> Unacknowledged = [];

        /// <summary>Tiles being sent or awaiting acknowledgement that the client dropped since they were handed over.</summary>
        public readonly HashSet<TerrainTileKey> DroppedInFlight = [];
        public long LastProgress = long.MinValue;

        /// <summary>Refusals not yet told, by reason, oldest reason first.</summary>
        public readonly Queue<Refusals> Refusals = new();
        public readonly Dictionary<string, Refusals> RefusalsByReason = new(StringComparer.Ordinal);
        public int RefusalCount;
        public bool RefusalsDropped;

        public Client(string uid) => this.Uid = uid;
    }

    private sealed class Refusals
    {
        public readonly string Reason;
        public readonly List<TerrainTileKey> Keys = [];
        public readonly HashSet<TerrainTileKey> Set = [];

        public Refusals(string reason) => this.Reason = reason;
    }
}
