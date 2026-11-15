using System.IO;
using System.Reflection;
using AlgernonsTerrainSampler.Tiles;
using ProtoBuf;

namespace AlgernonsTerrainSampler.Tests;

public class TileRequestTests
{
    private sealed class Source : ITerrainTileSource
    {
        public readonly List<(TerrainTileKey Key, int Priority)> Wants = [];
        public readonly List<TerrainTileKey> Drops = [];

        /// <summary>When set, the source cannot ask at all, and says this.</summary>
        public string? Cannot;

        public string? Want(TerrainTileKey key, int priority)
        {
            this.Wants.Add((key, priority));
            return this.Cannot;
        }

        public void Drop(TerrainTileKey key) => this.Drops.Add(key);
    }

    private static readonly TerrainTileKey Key = new(4, 10, 20);

    [Fact]
    public void Mods_asking_for_one_tile_ask_the_source_once_at_their_most_urgent()
    {
        Source source = new();
        TerrainTileRequests requests = new(source);
        long first = requests.Request(Key, 5, _ => { }, null);
        long second = requests.Request(Key, 9, _ => { }, null);
        long third = requests.Request(Key, 1, _ => { }, null);

        Assert.Equal([(Key, 5), (Key, 1)], source.Wants);
        requests.Cancel(first);
        requests.Cancel(second);
        Assert.Empty(source.Drops);
        requests.Cancel(third);
        Assert.Equal([Key], source.Drops);
        Assert.Equal(0, requests.Pending);
    }

    [Fact]
    public void Every_asker_gets_the_tile_even_if_one_of_them_throws()
    {
        TerrainTileRequests requests = new(new Source());
        int ready = 0;
        _ = requests.Request(Key, 0, _ => throw new InvalidOperationException("consumer bug"), null);
        _ = requests.Request(Key, 0, _ => ready++, null);
        requests.Complete(new TerrainTile(Key));

        Assert.Equal(1, ready);
        Assert.Equal(0, requests.Pending);
    }

    [Fact]
    public void A_refusal_reaches_every_asker_and_a_cancelled_one_hears_nothing()
    {
        TerrainTileRequests requests = new(new Source());
        List<string> reasons = [];
        _ = requests.Request(Key, 0, _ => { }, reasons.Add);
        long cancelled = requests.Request(Key, 0, _ => { }, reasons.Add);
        requests.Cancel(cancelled);
        requests.Fail(Key, "too far");

        Assert.Equal(["too far"], reasons);
    }

    [Fact]
    public void A_server_mod_asking_before_the_service_starts_gets_a_handle_and_a_refusal_it_can_cancel()
    {
        TerrainTileLocalSource local = new(() => "The terrain tile service has not started.");
        TerrainTileRequests requests = local.Requests;
        List<string> heard = [];
        bool inside = true, answeredInside = false;
        void Refused(string reason)
        {
            answeredInside |= inside;
            heard.Add(reason);
        }

        long kept = requests.Request(Key, 0, _ => heard.Add("tile"), Refused);
        long cancelled = requests.Request(new TerrainTileKey(4, 11, 20), 0, _ => heard.Add("tile"), Refused);
        inside = false;
        Assert.NotEqual(0, kept);
        Assert.NotEqual(0, cancelled);
        Assert.NotEqual(kept, cancelled);

        requests.Cancel(cancelled);
        Assert.Empty(heard);
        local.Tick();
        Assert.Equal(["The terrain tile service has not started."], heard);
        Assert.False(answeredInside);
        Assert.Equal(0, requests.Pending);
    }

    [Fact]
    public void A_server_mod_gets_its_tile_through_the_local_source_on_a_later_tick()
    {
        TerrainTileLocalSource local = new(() => "Not started.");
        TerrainTileRequests requests = local.Requests;
        FakeTerrain terrain = new();
        using TerrainTileService service = new(
            1024000,
            1024000,
            terrain.Sample,
            null,
            new TerrainTileService.Settings(1, 64, 1024, 4096),
            _ => TerrainTileOwner.Unlimited,
            (_, key, data) => local.Ready(key, data),
            (_, key, reason) => local.Refused(key, reason));
        local.Service = service;
        List<TerrainTile> tiles = [];
        List<string> refusals = [];
        _ = requests.Request(Key, 0, tiles.Add, refusals.Add);
        _ = requests.Request(new TerrainTileKey(3, 0, 0), 0, tiles.Add, refusals.Add);

        Wait.Until(() => tiles.Count + refusals.Count == 2, () =>
        {
            service.Tick();
            local.Tick();
        }, "both answers");
        Assert.Equal(Key, Assert.Single(tiles).Key);
        Assert.Contains("power of two", Assert.Single(refusals));
    }

    [Fact]
    public void A_refusal_decided_before_a_request_never_reaches_it()
    {
        Source source = new() { Cannot = "Not yet." };
        TerrainTileRequests requests = new(source);
        List<string> first = [], second = [];
        _ = requests.Request(Key, 0, _ => { }, first.Add);

        // Asked again once the source can ask, before the first refusal is told: asked afresh, and not refused.
        source.Cannot = null;
        _ = requests.Request(Key, 0, _ => { }, second.Add);
        requests.Tick();

        Assert.Equal(["Not yet."], first);
        Assert.Empty(second);
        Assert.Equal([(Key, 0), (Key, 0)], source.Wants);
        Assert.Equal(1, requests.Pending);
    }

    [Fact]
    public void A_server_mod_asking_again_as_the_service_starts_is_not_refused_for_before()
    {
        TerrainTileLocalSource local = new(() => "Not started.");
        FakeTerrain terrain = new();
        using TerrainTileService service = new(
            1024000,
            1024000,
            terrain.Sample,
            null,
            new TerrainTileService.Settings(1, 64, 1024, 4096),
            _ => TerrainTileOwner.Unlimited,
            (_, key, data) => local.Ready(key, data),
            (_, key, reason) => local.Refused(key, reason));
        List<string> refusals = [];
        List<TerrainTile> tiles = [];
        _ = local.Requests.Request(Key, 0, tiles.Add, refusals.Add);
        local.Service = service;
        _ = local.Requests.Request(Key, 0, tiles.Add, refusals.Add);

        Wait.Until(() => tiles.Count == 1, () =>
        {
            service.Tick();
            local.Tick();
        }, "the second request's tile");
        Assert.Equal(["Not started."], refusals);
    }

    [Fact]
    public void A_failing_tick_is_logged_once_tried_every_time_and_said_to_work_again()
    {
        List<string> errors = [], notes = [];
        TerrainTileTickGuard guard = new("the tick", errors.Add, notes.Add);
        int runs = 0;
        for (int i = 0; i < 50; i++)
        {
            guard.Run(() =>
            {
                runs++;
                throw new InvalidOperationException("stuck");
            });
        }

        Assert.Equal(50, runs);
        Assert.Contains("stuck", Assert.Single(errors));
        Assert.Equal(50, guard.Failing);

        guard.Run(() => runs++);
        Assert.Equal(0, guard.Failing);
        Assert.Contains("after 50 failed tick(s)", Assert.Single(notes));

        // A later run of failures is logged afresh.
        guard.Run(() => throw new InvalidOperationException("again"));
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void A_mod_without_a_reference_can_pass_an_action_of_object()
    {
        // As a consumer binding by reflection would: Action<object> for Action<TerrainTile>, by delegate variance.
        TerrainTileRequests requests = new(new Source());
        MethodInfo request = typeof(TerrainTileRequests).GetMethod("Request", BindingFlags.Instance | BindingFlags.Public)!;
        object? received = null;
        Action<object> ready = tile => received = tile;
        Action<string> refused = _ => { };
        _ = request.Invoke(requests, [Key, 0, ready, refused]);
        requests.Complete(new TerrainTile(Key));

        TerrainTile tile = Assert.IsType<TerrainTile>(received);
        FieldInfo height = tile.GetType().GetField("Height")!;
        Assert.Equal(TerrainTileKey.ColumnCount, ((short[])height.GetValue(tile)!).Length);
    }
}

/// <summary>A server and clients joined by real protobuf serialisation, as the channel would carry them.</summary>
public class TileNetworkTests
{
    private const int World = 1024000;
    private const string Player = "player-uid";
    private static readonly string Identity = TerrainTileIdentity.Hash(writer => writer.Write("network world"));

    private static T Wire<T>(T message)
    {
        using MemoryStream stream = new();
        Serializer.Serialize(stream, message);
        stream.Position = 0;
        return Serializer.Deserialize<T>(stream);
    }

    private sealed class Server : IDisposable
    {
        public readonly FakeTerrain Terrain;
        public readonly Queue<object> ToClient = new();
        public readonly TerrainTileService Service;
        public readonly TerrainTileSender Sender;
        public (double X, double Z) Position = (512000, 512000);
        private readonly int maxWanted;

        public Server(int maxWanted = 1024, bool noisy = false)
        {
            this.maxWanted = maxWanted;
            this.Terrain = new FakeTerrain { Noisy = noisy };
            this.Sender = new TerrainTileSender(
                (_, data) => this.ToClient.Enqueue(Wire(data)),
                (_, refused) => this.ToClient.Enqueue(Wire(refused)),
                (_, progress) => this.ToClient.Enqueue(Wire(progress)));
            this.Service = new TerrainTileService(
                World,
                World,
                this.Terrain.Sample,
                null,
                new TerrainTileService.Settings(2, 64, maxWanted, 4096),
                _ => new TerrainTileOwner(true, "", true, this.Position.X, this.Position.Z),
                this.Sender.Enqueue,
                this.Sender.Refuse,
                queue: this.Sender);
        }

        public TerrainTileStatus Status => Wire(new TerrainTileStatus { Available = true, Identity = Identity, MaxWantedTiles = this.maxWanted, MaxDistancePerStep = 4096 });

        public void Tick(long now)
        {
            this.Service.Tick();
            this.Sender.Tick(64 * 1024, now, this.Service.WantedBy, this.Service.Running, this.Service.Waiting);
        }

        public void Dispose() => this.Service.Dispose();
    }

    private sealed class Client : IDisposable
    {
        public readonly Queue<object> ToServer = new();
        public readonly TerrainTileClientCore Core;
        public readonly List<TerrainTile> Tiles = [];
        public readonly List<string> Refusals = [];
        public int WantsSent;
        public int Acknowledged;
        public int Hellos;

        /// <summary>Decodes waiting to run, when the client was made to hold them: the test runs them when it chooses.</summary>
        public readonly List<Action> Decodes = [];

        public Client(string? diskRoot, bool holdDecodes = false)
        {
            this.Core = new TerrainTileClientCore(
                want =>
                {
                    this.WantsSent++;
                    this.ToServer.Enqueue(Wire(want));
                },
                identity => diskRoot == null ? null : new TerrainTileDiskCache(diskRoot, identity, 64),
                sendHello: () => this.Hellos++,
                sendReceived: received =>
                {
                    this.Acknowledged++;
                    this.ToServer.Enqueue(Wire(received));
                },
                background: holdDecodes ? this.Decodes.Add : null);
        }

        public TerrainTileWant NextWant() => (TerrainTileWant)this.ToServer.Dequeue();

        public void RunDecodes()
        {
            foreach (Action decode in this.Decodes)
                decode();

            this.Decodes.Clear();
        }

        public void Ask(TerrainTileKey key, int priority = 0) => _ = this.Core.Requests.Request(key, priority, this.Tiles.Add, this.Refusals.Add);

        public void Dispose() => this.Core.Dispose();
    }

    // Runs both sides, carrying messages between them, until every request is answered.
    private static void Run(Server server, Client client, int answers)
    {
        long now = 0;
        Wait.Until(() => client.Tiles.Count + client.Refusals.Count >= answers, () =>
        {
            now += 50;
            client.Core.Tick();
            while (client.ToServer.TryDequeue(out object? message))
            {
                switch (message)
                {
                    case TerrainTileWant want: server.Sender.OnWant(Player, want, server.Service, "No service."); break;
                    case TerrainTileReceived received: server.Sender.OnReceived(Player, received); break;
                }
            }

            server.Tick(now);
            while (server.ToClient.TryDequeue(out object? message))
            {
                switch (message)
                {
                    case TerrainTileData data: client.Core.OnData(data); break;
                    case TerrainTileRefused refused: client.Core.OnRefused(refused); break;
                    case TerrainTileProgress progress: client.Core.OnProgress(progress); break;
                }
            }
        }, $"{answers} answer(s)");
    }

    private static List<TerrainTileKey> Square(int step, int radius)
    {
        TerrainTileKey centre = TerrainTileKey.Containing(step, 512000, 512000);
        List<TerrainTileKey> keys = [];
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
                keys.Add(new TerrainTileKey(step, centre.TileX + dx, centre.TileZ + dz));
        }

        return keys;
    }

    [Fact]
    public void A_client_gets_the_tiles_round_it_whole_and_exact()
    {
        using Server server = new();
        using Client client = new(null);
        client.Core.OnStatus(server.Status);
        Assert.True(client.Core.Available);

        foreach (TerrainTileKey key in Square(4, 1))
            client.Ask(key);

        Run(server, client, 9);
        Assert.Empty(client.Refusals);
        Assert.Equal(9, client.Tiles.Count);
        Assert.Equal(9, client.Acknowledged);
        Assert.Equal(9L * TerrainTileKey.ColumnCount, server.Terrain.Samples);
        foreach (TerrainTile tile in client.Tiles)
        {
            int last = TerrainTile.Index(TerrainTileKey.Cells, TerrainTileKey.Cells);
            Assert.Equal(FakeTerrain.Column(tile.Key.SampleX(TerrainTileKey.Cells), tile.Key.SampleZ(TerrainTileKey.Cells)).Height, tile.Height[last]);
        }
    }

    [Fact]
    public void A_large_tile_goes_in_fragments_under_the_budget_and_clients_take_turns()
    {
        List<(string Uid, TerrainTileData Data)> sent = [];
        TerrainTileSender sender = new((uid, data) => sent.Add((uid, Wire(data))), (_, _) => { }, (_, _) => { });
        byte[] big = new byte[100 * 1024];
        new Random(7).NextBytes(big);
        sender.Enqueue("a", new TerrainTileKey(4, 1, 1), big);
        sender.Enqueue("b", new TerrainTileKey(4, 2, 2), big);

        sender.Tick(64 * 1024, 0, _ => 0, 0, 0);
        Assert.Equal(["a", "b"], sent.Select(s => s.Uid));
        Assert.All(sent, s => Assert.Equal(TerrainTileProtocol.FragmentBytes, s.Data.Data!.Length));

        for (int tick = 1; tick < 10; tick++)
            sender.Tick(64 * 1024, tick * 50, _ => 0, 0, 0);

        foreach (string uid in new[] { "a", "b" })
        {
            byte[] joined = [.. sent.Where(s => s.Uid == uid).OrderBy(s => s.Data.Offset).SelectMany(s => s.Data.Data!)];
            Assert.Equal(big, joined);
        }

        Assert.Equal(2, sender.TilesSent);
    }

    [Fact]
    public void A_reconnecting_client_reads_its_own_cache_and_asks_the_server_for_nothing()
    {
        using TemporaryFolder folder = new();
        using Server server = new();
        using (Client first = new(folder.Path))
        {
            first.Core.OnStatus(server.Status);
            foreach (TerrainTileKey key in Square(8, 1))
                first.Ask(key);

            Run(server, first, 9);
            Wait.Until(() => first.Core.Disk!.Count == 9, () => { }, "the client's disk writes");
        }

        long sampled = server.Terrain.Samples;
        using Client again = new(folder.Path);
        again.Core.OnStatus(server.Status);
        Wait.Until(() => again.Core.Disk!.Ready, () => { }, "the client's disk index");
        foreach (TerrainTileKey key in Square(8, 1))
            again.Ask(key);

        Run(server, again, 9);
        Assert.Equal(9, again.Core.FromDisk);
        Assert.Equal(0, again.WantsSent);
        Assert.Equal(sampled, server.Terrain.Samples);
    }

    [Fact]
    public void Two_mods_on_one_client_download_a_tile_once()
    {
        using Server server = new();
        using Client client = new(null);
        client.Core.OnStatus(server.Status);
        TerrainTileKey key = Square(4, 0)[0];
        client.Ask(key, 3);
        client.Ask(key, 1);

        Run(server, client, 2);
        Assert.Equal(1, client.Core.Received);
        Assert.Same(client.Tiles[0], client.Tiles[1]);
    }

    [Fact]
    public void A_refusal_crosses_the_wire_with_its_reason()
    {
        using Server server = new();
        using Client client = new(null);
        client.Core.OnStatus(server.Status);
        TerrainTileKey far = TerrainTileKey.Containing(1, 512000 + 20000, 512000);
        client.Ask(far);

        Run(server, client, 1);
        Assert.Contains("4096 blocks", Assert.Single(client.Refusals));
    }

    [Fact]
    public void A_cancelled_tile_is_dropped_on_the_server_too()
    {
        using Server server = new();
        using Client client = new(null);
        client.Core.OnStatus(server.Status);
        TerrainTileKey key = Square(4, 0)[0];
        long handle = client.Core.Requests.Request(key, 0, client.Tiles.Add, client.Refusals.Add);
        client.Core.Tick();
        server.Service.Apply(Player, client.NextWant());
        Assert.Equal(1, server.Service.WantedBy(Player));

        client.Core.Requests.Cancel(handle);
        client.Core.Tick();
        server.Service.Apply(Player, client.NextWant());
        Assert.Equal(0, server.Service.WantedBy(Player));
    }

    [Fact]
    public void A_client_keeps_no_more_than_the_servers_limit_on_its_books_most_urgent_first()
    {
        using Client client = new(null);
        client.Core.OnStatus(Wire(new TerrainTileStatus { Available = true, Identity = Identity, MaxWantedTiles = 4 }));
        List<TerrainTileKey> keys = Square(4, 1);
        long[] handles = [.. keys.Select((key, i) => client.Core.Requests.Request(key, 9 - i, client.Tiles.Add, client.Refusals.Add))];

        client.Core.Tick();
        TerrainTileWant first = client.NextWant();
        Assert.Equal([keys[8], keys[7], keys[6], keys[5]], first.Adds().Select(a => a.Key));
        Assert.Equal([1, 2, 3, 4], first.Adds().Select(a => a.Priority));
        Assert.Equal(5, client.Core.Held);

        client.Core.Tick();
        Assert.Empty(client.ToServer);

        // A refusal frees room for the next most urgent.
        client.Core.OnRefused(TerrainTileRefused.For([keys[8]], "No."));
        client.Core.Tick();
        Assert.Equal([keys[4]], client.NextWant().Adds().Select(a => a.Key));

        // So does a cancelled tile, whose drop goes before the add that takes its place.
        client.Core.Requests.Cancel(handles[7]);
        client.Core.Tick();
        TerrainTileWant swap = client.NextWant();
        Assert.Equal([keys[7]], swap.Drops());
        Assert.Equal([keys[3]], swap.Adds().Select(a => a.Key));

        // A held tile cancelled is never asked for.
        client.Core.Requests.Cancel(handles[0]);
        Assert.Equal(2, client.Core.Held);
        Assert.Equal(4, client.Core.OnServer);
        Assert.Equal(["No."], client.Refusals);
    }

    [Fact]
    public void A_client_asking_for_more_than_the_limit_gets_every_tile_in_turn_and_none_refused()
    {
        using Server server = new(maxWanted: 4);
        using Client client = new(null);
        client.Core.OnStatus(server.Status);
        foreach (TerrainTileKey key in Square(4, 2).Take(12))
            client.Ask(key);

        Run(server, client, 12);
        Assert.Empty(client.Refusals);
        Assert.Equal(12, client.Tiles.Count);
    }

    private static TerrainTileData Piece(TerrainTileKey key, int total, int offset, int length)
        => Wire(new TerrainTileData { Step = key.Step, TileX = key.TileX, TileZ = key.TileZ, Total = total, Offset = offset, Data = new byte[length] });

    // The whole of a real tile in one piece, as the server would send a small one.
    private static TerrainTileData Whole(TerrainTileKey key)
    {
        byte[] encoded = TerrainTileCodec.Encode(new TerrainTile(key));
        return Wire(new TerrainTileData { Step = key.Step, TileX = key.TileX, TileZ = key.TileZ, Total = encoded.Length, Offset = 0, Data = encoded });
    }

    // A tile asked of the server and arrived whole, its decode held; then cancelled and asked for again.
    private static (Client Client, TerrainTileKey Key) AskedAgainWhileDecoding()
    {
        Client client = new(null, holdDecodes: true);
        client.Core.OnStatus(Wire(new TerrainTileStatus { Available = true, Identity = Identity }));
        TerrainTileKey key = Square(4, 0)[0];
        long first = client.Core.Requests.Request(key, 0, client.Tiles.Add, client.Refusals.Add);
        client.Core.Tick();
        _ = client.NextWant();
        client.Core.OnData(Whole(key));
        _ = client.ToServer.Dequeue(); // Its acknowledgement.
        Assert.Single(client.Decodes);

        client.Core.Requests.Cancel(first);
        client.Ask(key);
        return (client, key);
    }

    [Fact]
    public void A_client_sends_no_change_of_priority_for_a_tile_already_arriving()
    {
        using Client client = new(null);
        client.Core.OnStatus(Wire(new TerrainTileStatus { Available = true, Identity = Identity }));
        TerrainTileKey arriving = Square(4, 1)[0], waiting = Square(4, 1)[1];
        client.Ask(arriving, 5);
        client.Ask(waiting, 5);
        client.Core.Tick();
        _ = client.NextWant();
        client.Core.OnData(Piece(arriving, 2000, 0, 1000));

        client.Ask(arriving, 1);
        client.Ask(waiting, 1);
        client.Core.Tick();
        TerrainTileWant sooner = client.NextWant();
        Assert.Equal([(waiting, 1)], sooner.Adds());
        Assert.Empty(client.ToServer);
    }

    [Fact]
    public void A_tile_dropped_and_asked_for_again_goes_as_a_drop_then_an_add()
    {
        using Client client = new(null);
        client.Core.OnStatus(Wire(new TerrainTileStatus { Available = true, Identity = Identity }));
        TerrainTileKey key = Square(4, 0)[0];
        long first = client.Core.Requests.Request(key, 0, client.Tiles.Add, client.Refusals.Add);
        client.Core.Tick();
        _ = client.NextWant();

        client.Core.Requests.Cancel(first);
        client.Ask(key);
        client.Core.Tick();
        TerrainTileWant again = client.NextWant();
        Assert.Equal([key], again.Drops());
        Assert.Equal([key], again.Adds().Select(a => a.Key));
        Assert.Equal(1, client.Core.OnServer);
    }

    [Fact]
    public void A_tile_let_go_while_it_arrives_and_asked_for_again_is_sent_again()
    {
        // Tiles of some 180 KiB, six pieces each, two a tick.
        using Server server = new(noisy: true);
        using Client client = new(null);
        client.Core.OnStatus(server.Status);
        TerrainTileKey key = Square(4, 0)[0];
        long first = client.Core.Requests.Request(key, 0, client.Tiles.Add, client.Refusals.Add);
        client.Core.Tick();
        server.Sender.OnWant(Player, client.NextWant(), server.Service, "");
        long now = 0;
        Wait.Until(() => server.ToClient.Any(m => m is TerrainTileData), () => server.Tick(now += 50), "the first pieces");

        // Cancelled before its first pieces arrive, so they are let go; asked for again before the rest do.
        client.Core.Requests.Cancel(first);
        while (server.ToClient.TryDequeue(out object? message))
            client.Core.OnData((TerrainTileData)message);

        Assert.Equal(0, client.Core.Assembling);
        client.Ask(key);
        Run(server, client, 1);
        Assert.Equal(key, Assert.Single(client.Tiles).Key);
        Assert.Empty(client.Refusals);
    }

    [Fact]
    public void A_tile_asked_for_again_while_it_decodes_is_answered_by_the_decode_when_that_comes_first()
    {
        (Client client, TerrainTileKey key) = AskedAgainWhileDecoding();
        using (client)
        {
            Assert.Equal(1, client.Core.Held);
            client.RunDecodes();
            client.Core.Tick();

            Assert.Equal(key, Assert.Single(client.Tiles).Key);
            Assert.Equal(0, client.Core.Held);
            Assert.Equal(0, client.Core.OnServer);
            Assert.Equal(0, client.Core.InFlight);
            Assert.Empty(client.ToServer); // Never asked of the server, so nothing to drop either.
        }
    }

    [Fact]
    public void A_tile_asked_for_again_while_it_decodes_is_answered_by_the_decode_when_the_tick_comes_first()
    {
        (Client client, TerrainTileKey key) = AskedAgainWhileDecoding();
        using (client)
        {
            client.Core.Tick();
            Assert.Equal([key], client.NextWant().Adds().Select(a => a.Key));
            Assert.Equal(1, client.Core.OnServer);

            client.RunDecodes();
            client.Core.Tick();
            Assert.Equal(key, Assert.Single(client.Tiles).Key);
            Assert.Equal(0, client.Core.OnServer);
            Assert.Equal(0, client.Core.InFlight);

            // The server, asked again, is told to forget it; and its copy, when it comes, is acknowledged and let go.
            TerrainTileWant drop = client.NextWant();
            Assert.Equal([key], drop.Drops());
            Assert.Empty(drop.Adds());
            client.Core.OnData(Whole(key));
            client.Core.Tick();
            Assert.Single(client.Tiles);
            Assert.Equal(0, client.Core.OnServer);
        }
    }

    [Fact]
    public void A_client_drops_tiles_it_never_asked_for_but_still_acknowledges_them()
    {
        using Client client = new(null);
        client.Core.OnStatus(Wire(new TerrainTileStatus { Available = true, Identity = Identity }));
        TerrainTileKey unasked = Square(4, 0)[0];
        for (int tile = 0; tile < 100; tile++)
        {
            TerrainTileKey key = unasked with { TileX = unasked.TileX + tile };
            client.Core.OnData(Piece(key, 64 * 1024, 0, 32 * 1024));
            client.Core.OnData(Piece(key, 64 * 1024, 32 * 1024, 32 * 1024));
        }

        Assert.Equal(0, client.Core.Assembling);
        Assert.Equal(0, client.Core.Received);
        Assert.Equal(100, client.Acknowledged);
    }

    [Fact]
    public void A_client_keeps_no_more_than_four_tiles_part_assembled()
    {
        using Client client = new(null);
        client.Core.OnStatus(Wire(new TerrainTileStatus { Available = true, Identity = Identity }));
        List<TerrainTileKey> keys = [.. Square(4, 1).Take(6)];
        foreach (TerrainTileKey key in keys)
            client.Ask(key);

        client.Core.Tick();
        foreach (TerrainTileKey key in keys)
            client.Core.OnData(Piece(key, 2000, 0, 1000));

        Assert.Equal(4, client.Core.Assembling);
        foreach (TerrainTileKey key in keys)
            client.Core.OnData(Piece(key, 2000, 1000, 1000));

        // The two started first were let go; the other four arrived whole, and every one was acknowledged.
        Assert.Equal(4, client.Core.Received);
        Assert.Equal(0, client.Core.Assembling);
        Assert.Equal(6, client.Acknowledged);
    }

    [Fact]
    public void A_consumer_asking_again_when_tiles_become_available_is_not_refused_for_before()
    {
        using Client client = new(null);
        TerrainTileKey key = Square(4, 0)[0];
        client.Ask(key);

        // As the documentation suggests: ask again when AvailabilityChanged says tiles can be had.
        client.Core.AvailabilityChanged += () => client.Ask(key);
        client.Core.OnStatus(Wire(new TerrainTileStatus { Available = true, Identity = Identity }));
        client.Core.Tick();

        Assert.Equal(["Waiting for the server."], client.Refusals);
        Assert.Equal([key], client.NextWant().Adds().Select(a => a.Key));
        Assert.Equal(1, client.Core.Requests.Pending);
    }

    [Fact]
    public void Without_a_server_requests_are_refused_on_the_next_tick_with_the_reason()
    {
        using Client client = new(null);
        client.Core.SetWaitingReason("No sampler here.");
        bool answeredInside = false;
        bool insideRequest = true;
        _ = client.Core.Requests.Request(new TerrainTileKey(4, 1, 1), 0, _ => { }, reason =>
        {
            answeredInside = insideRequest;
            client.Refusals.Add(reason);
        });
        insideRequest = false;

        Assert.Empty(client.Refusals);
        client.Core.Tick();
        Assert.Equal(["No sampler here."], client.Refusals);
        Assert.False(answeredInside);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(123456789L)]
    [InlineData(long.MaxValue - 100000)]
    public void A_connected_client_says_hello_at_once_and_again_until_answered(long start)
    {
        using Server server = new();
        using Client client = new(null);
        client.Core.Tick(connected: true, start);
        Assert.Equal(1, client.Hellos);

        client.Core.Tick(connected: true, start + 4999);
        Assert.Equal(1, client.Hellos);
        client.Core.Tick(connected: true, start + 5000);
        Assert.Equal(2, client.Hellos);

        client.Core.OnStatus(server.Status);
        client.Core.Tick(connected: true, start + 20000);
        Assert.Equal(2, client.Hellos);
        Assert.True(client.Core.Available);
    }

    [Fact]
    public void A_client_whose_channel_never_connects_says_why_after_a_while()
    {
        using Client client = new(null);
        client.Core.Tick(connected: false, 1000);
        Assert.Equal("Waiting for the server.", client.Core.UnavailableReason);

        client.Core.Tick(connected: false, 11000);
        Assert.Contains("does not run Algernon's Terrain Sampler 2.0.0 or later", client.Core.UnavailableReason);
        Assert.Equal(0, client.Hellos);
    }

    [Fact]
    public void A_client_that_loses_its_channel_refuses_what_it_waited_for_and_greets_the_next_one()
    {
        using Server server = new();
        using Client client = new(null);
        client.Core.Tick(connected: true, 0);
        client.Core.OnStatus(server.Status);
        client.Ask(Square(4, 0)[0]);
        client.Core.Tick(connected: true, 50);

        client.Core.Tick(connected: false, 100);
        Assert.False(client.Core.Available);
        Assert.Equal(["Disconnected from the server."], client.Refusals);

        client.Core.Tick(connected: true, 150);
        Assert.Equal(2, client.Hellos);
    }

    [Fact]
    public void A_server_that_says_no_is_heard_and_its_reason_kept()
    {
        using Client client = new(null);
        client.Core.Tick(connected: true, 0);
        client.Core.OnStatus(Wire(new TerrainTileStatus { Available = false, Reason = "Not for you." }));
        Assert.False(client.Core.Available);
        Assert.Equal("Not for you.", client.Core.UnavailableReason);

        client.Core.Tick(connected: true, 60000);
        Assert.Equal(1, client.Hellos);
    }

    [Fact]
    public void A_server_whose_sampler_goes_off_and_comes_back_is_heard_and_the_cache_kept()
    {
        using TemporaryFolder folder = new();
        int opened = 0;
        using TerrainTileClientCore core = new(_ => { }, identity =>
        {
            opened++;
            return new TerrainTileDiskCache(folder.Path, identity, 64);
        });
        List<string> refusals = [];
        core.OnStatus(Wire(new TerrainTileStatus { Available = true, Identity = Identity }));
        _ = core.Requests.Request(new TerrainTileKey(4, 1, 1), 0, _ => { }, refusals.Add);

        core.OnStatus(Wire(new TerrainTileStatus { Available = false, Reason = "The terrain sampler failed on 8 tiles in a row." }));
        Assert.False(core.Available);
        Assert.Equal(["The terrain sampler failed on 8 tiles in a row."], refusals);

        core.OnStatus(Wire(new TerrainTileStatus { Available = true, Identity = Identity }));
        Assert.True(core.Available);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void A_server_on_another_protocol_is_not_used()
    {
        using Client client = new(null);
        client.Core.OnStatus(Wire(new TerrainTileStatus { Protocol = TerrainTileProtocol.Version + 1, Available = true, Identity = Identity }));
        Assert.False(client.Core.Available);
        Assert.Contains("protocol", client.Core.UnavailableReason);
    }
}
