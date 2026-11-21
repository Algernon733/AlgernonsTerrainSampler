using AlgernonsTerrainSampler.Tiles;

namespace AlgernonsTerrainSampler.Tests;

/// <summary>The server's side of the channel against clients that ask for too much, or ask badly.</summary>
public class TileSenderTests
{
    private const int World = 1024000;
    private const string Player = "player";
    private const string Warm = "warm";

    private sealed class Harness : IDisposable
    {
        public readonly FakeTerrain Terrain;
        public readonly List<(string Uid, object Message)> Sent = [];
        public readonly List<TerrainTileKey> Warmed = [];
        public readonly TerrainTileSender Sender;
        public readonly TerrainTileService Service;
        public readonly TerrainTileDiskCache? Disk;
        public TerrainTileOwner Owner = new(true, "", true, 512000, 512000);
        public int OwnerLookups;
        public long Now;
        private int acknowledged;

        public Harness(int maxWanted = 1024, string? diskRoot = null, int memoryMegabytes = 64, bool noisy = false)
        {
            this.Terrain = new FakeTerrain { Noisy = noisy };
            this.Disk = diskRoot == null ? null : new TerrainTileDiskCache(diskRoot, TerrainTileIdentity.Hash(writer => writer.Write("sender tests")), 64);
            this.Sender = new TerrainTileSender(
                (uid, data) => this.Sent.Add((uid, data)),
                (uid, refused) => this.Sent.Add((uid, refused)),
                (uid, progress) => this.Sent.Add((uid, progress)));

            // The player is known to the sender, as one is once its hello or first want has arrived.
            this.Sender.Touch(Player);
            this.Service = new TerrainTileService(
                World,
                World,
                this.Terrain.Sample,
                this.Disk,
                new TerrainTileService.Settings(1, memoryMegabytes, maxWanted, 4096),
                owner =>
                {
                    this.OwnerLookups++;
                    return owner == Warm ? TerrainTileOwner.Unlimited : this.Owner;
                },
                (owner, key, data) =>
                {
                    if (owner == Warm)
                        this.Warmed.Add(key);
                    else
                        this.Sender.Enqueue(owner, key, data);
                },
                this.Sender.Refuse,
                clock: () => this.Now,
                queue: this.Sender);
        }

        public IEnumerable<TerrainTileRefused> Refusals => this.Sent.Select(s => s.Message).OfType<TerrainTileRefused>();

        public IEnumerable<TerrainTileData> Data => this.Sent.Select(s => s.Message).OfType<TerrainTileData>();

        /// <summary>Makes the tiles and keeps them in memory, as a mod on the server asking for them one at a time would.</summary>
        public void MakeInMemory(params TerrainTileKey[] keys)
        {
            foreach (TerrainTileKey key in keys)
            {
                this.Service.Want(Warm, key, 0);
                Wait.Until(() => this.Warmed.Contains(key), this.Service.Tick, "the tile to be made");
            }
        }

        public void Tick(int budget = 1 << 20)
        {
            this.Service.Tick();
            this.Sender.Tick(budget, this.Now, this.Service.WantedBy, this.Service.Running, this.Service.Waiting);
        }

        /// <summary>The player says it has every tile sent to it whole so far.</summary>
        public void AcknowledgeAll()
        {
            TerrainTileData[] whole = [.. this.Data.Where(d => d.Offset + d.Data!.Length == d.Total)];
            for (; this.acknowledged < whole.Length; this.acknowledged++)
                this.Sender.OnReceived(Player, new TerrainTileReceived { Step = whole[this.acknowledged].Step, TileX = whole[this.acknowledged].TileX, TileZ = whole[this.acknowledged].TileZ });
        }

        public void WaitForDisk() => Wait.Until(() => this.Disk == null || this.Disk.Ready, () => { }, "the disk index");

        public void Dispose() => this.Service.Dispose();
    }

    private static TerrainTileKey Near(int dx = 0, int dz = 0) => new(4, 1000 + dx, 1000 + dz);

    private static TerrainTileWant Want(IEnumerable<TerrainTileKey> adds, IEnumerable<TerrainTileKey>? drops = null, bool reset = false)
    {
        TerrainTileKey[] added = [.. adds];
        TerrainTileKey[] dropped = [.. drops ?? []];
        return new TerrainTileWant
        {
            Reset = reset,
            AddStep = [.. added.Select(k => k.Step)],
            AddX = [.. added.Select(k => k.TileX)],
            AddZ = [.. added.Select(k => k.TileZ)],
            AddPriority = [.. added.Select((_, i) => i)],
            DropStep = [.. dropped.Select(k => k.Step)],
            DropX = [.. dropped.Select(k => k.TileX)],
            DropZ = [.. dropped.Select(k => k.TileZ)]
        };
    }

    [Fact]
    public void Copies_of_a_cached_tile_are_queued_once_and_sent_once()
    {
        using Harness h = new();
        h.MakeInMemory(Near());
        for (int message = 0; message < 10; message++)
            h.Service.Apply(Player, Want(Enumerable.Repeat(Near(), TerrainTileProtocol.MaxKeysPerWant)));

        Assert.Equal(1, h.Sender.Count(Player));
        Assert.Empty(h.Refusals);
        for (int tick = 0; tick < 10; tick++)
            h.Tick();

        Assert.Equal(1, h.Sender.TilesSent);
    }

    [Fact]
    public void Dropped_and_reset_tiles_leave_the_queue_before_they_start()
    {
        using Harness h = new();
        h.MakeInMemory(Near(0), Near(1), Near(2), Near(3));
        h.Service.Apply(Player, Want([Near(0), Near(1), Near(2)]));
        Assert.Equal([Near(0), Near(1)], h.Sender.Keys(Player)); // Room for two; the third waits in the service.
        Assert.Equal(1, h.Service.WantedBy(Player));

        h.Service.Apply(Player, Want([], drops: [Near(1)]));
        Assert.Equal([Near(0)], h.Sender.Keys(Player));

        h.Service.Apply(Player, Want([Near(3)], reset: true));
        Assert.Equal([Near(3)], h.Sender.Keys(Player));
        Assert.Equal(0, h.Service.WantedBy(Player));

        for (int tick = 0; tick < 10; tick++)
            h.Tick();

        Assert.Equal(1, h.Sender.TilesSent);
        Assert.All(h.Data, d => Assert.Equal(Near(3), new TerrainTileKey(d.Step, d.TileX, d.TileZ)));
    }

    [Fact]
    public void Queued_tiles_count_against_the_limit_until_they_go()
    {
        using Harness h = new(maxWanted: 2);
        h.MakeInMemory(Near(0), Near(1), Near(2));
        h.Service.Apply(Player, Want([Near(0), Near(1), Near(2)]));

        Assert.Equal(2, h.Sender.Count(Player));
        h.Tick();
        TerrainTileRefused refused = Assert.Single(h.Refusals);
        Assert.Contains("More than 2", refused.Reason);
        Assert.Equal([Near(2)], refused.Keys());
        Assert.Equal(0, h.Sender.Count(Player));
        h.Service.Apply(Player, Want([Near(2)]));
        Assert.Equal(1, h.Sender.Count(Player));
    }

    [Fact]
    public void A_player_who_loses_permission_loses_what_was_queued_for_them()
    {
        using Harness h = new();
        h.MakeInMemory(Near(0), Near(1));
        h.Service.Apply(Player, Want([Near(0), Near(1)]));
        Assert.Equal(2, h.Sender.Count(Player));

        h.Owner = TerrainTileOwner.Denied("No longer.");
        h.Now += 5000;
        h.Tick();

        Assert.Equal(0, h.Sender.TilesSent);
        TerrainTileRefused refused = Assert.Single(h.Refusals);
        Assert.Equal("No longer.", refused.Reason);
        Assert.Equal([Near(0), Near(1)], refused.Keys().OrderBy(k => k.TileX));
    }

    [Fact]
    public void A_client_that_never_acknowledges_gets_no_more_than_the_window()
    {
        List<(string Uid, TerrainTileKey Key)> sent = [];
        TerrainTileSender sender = new((uid, data) => sent.Add((uid, new TerrainTileKey(data.Step, data.TileX, data.TileZ))), (_, _) => { }, (_, _) => { });
        for (int i = 0; i < 10; i++)
        {
            sender.Enqueue("slow", Near(i), new byte[1000]);
            sender.Enqueue("fast", Near(i), new byte[1000]);
        }

        for (int tick = 0; tick < 20; tick++)
        {
            sender.Tick(256 * 1024, tick * 50, _ => 0, 0, 0);
            foreach ((string uid, TerrainTileKey key) in sent.Where(s => s.Uid == "fast").ToArray())
                sender.OnReceived("fast", new TerrainTileReceived { Step = key.Step, TileX = key.TileX, TileZ = key.TileZ });
        }

        Assert.Equal(TerrainTileProtocol.WindowTiles, sent.Count(s => s.Uid == "slow"));
        Assert.Equal(10, sent.Count(s => s.Uid == "fast"));
        Assert.Equal(10, sender.Sending("slow"));

        // Acknowledgements of tiles never sent do not open the window.
        sender.OnReceived("slow", new TerrainTileReceived { Step = 4, TileX = 1, TileZ = 1 });
        sender.Tick(256 * 1024, 2000, _ => 0, 0, 0);
        Assert.Equal(TerrainTileProtocol.WindowTiles, sent.Count(s => s.Uid == "slow"));

        sender.OnReceived("slow", new TerrainTileReceived { Step = 4, TileX = Near(0).TileX, TileZ = Near(0).TileZ });
        sender.Tick(256 * 1024, 2050, _ => 0, 0, 0);
        Assert.Equal([Near(0), Near(1), Near(2)], sent.Where(s => s.Uid == "slow").Select(s => s.Key));
    }

    [Fact]
    public void A_client_whose_tiles_are_still_being_made_hears_how_they_are_getting_on_every_five_seconds()
    {
        using Harness h = new();
        List<TerrainTileProgress> Progress() => [.. h.Sent.Select(s => s.Message).OfType<TerrainTileProgress>()];

        // Nothing queued for it yet, only wanted: the service is not ticked, so the tiles stay wanted.
        h.Sender.OnWant(Player, Want([Near(0), Near(1)]), h.Service, "");
        void Tick(long now) => h.Sender.Tick(1 << 20, now, h.Service.WantedBy, h.Service.Running, h.Service.Waiting);
        Tick(100000);
        Tick(104999);
        Assert.Empty(Progress());

        Tick(105000);
        TerrainTileProgress progress = Assert.Single(Progress());
        Assert.Equal(2, progress.Wanted);
        Assert.Equal(0, progress.Sending);

        Tick(109999);
        Assert.Single(Progress());
        Tick(110000);
        Assert.Equal(2, Progress().Count);

        // Once nothing waits, the reports stop.
        h.Service.Apply(Player, Want([], drops: [Near(0), Near(1)]));
        Tick(200000);
        Assert.Equal(2, Progress().Count);
    }

    [Fact]
    public void A_client_that_never_acknowledges_costs_the_sender_a_few_tiles_and_the_rest_wait_in_the_service()
    {
        using Harness h = new();
        TerrainTileKey[] keys = [.. Enumerable.Range(0, 40).Select(i => Near(i % 8, i / 8))];
        h.MakeInMemory(keys);
        h.Service.Apply(Player, Want(keys));
        for (int tick = 0; tick < 30; tick++)
            h.Tick();

        int largest = h.Data.Max(d => d.Total);
        Assert.Equal(TerrainTileProtocol.WindowTiles, h.Sender.TilesSent);
        Assert.Equal(TerrainTileSender.QueueTiles, h.Sender.Count(Player));
        Assert.True(h.Sender.BytesHeld(Player) <= TerrainTileSender.QueueTiles * (long)largest);
        Assert.Equal(40 - TerrainTileProtocol.WindowTiles - TerrainTileSender.QueueTiles, h.Service.WantedBy(Player));

        // As it acknowledges, the rest follow, most urgent first, each once.
        for (int tick = 0; tick < 200 && h.Sender.TilesSent < 40; tick++)
        {
            h.AcknowledgeAll();
            h.Tick();
        }

        TerrainTileKey[] sent = [.. h.Data.Where(d => d.Offset == 0).Select(d => new TerrainTileKey(d.Step, d.TileX, d.TileZ))];
        Assert.Equal(keys, sent);
        Assert.Equal(0, h.Service.WantedBy(Player));
    }

    [Fact]
    public void Tiles_a_slow_client_waits_for_stay_within_the_memory_budget_and_are_read_again_from_disk()
    {
        using TemporaryFolder folder = new();
        using Harness h = new(diskRoot: folder.Path, memoryMegabytes: 1, noisy: true);
        h.WaitForDisk();
        TerrainTileKey[] keys = [.. Enumerable.Range(0, 30).Select(i => Near(i % 6, i / 6))];
        h.Service.Apply(Player, Want(keys));

        // Made ahead onto disk, while the client takes nothing past its window.
        Wait.Until(() => h.Service.Generated == keys.Length && h.Service.Running == 0, () => h.Tick(), "every tile made");
        h.Tick();
        int largest = h.Data.Max(d => d.Total);
        Assert.True(largest > 100 * 1024, $"tiles of {largest} bytes are too small to fill the budget");
        Assert.True(h.Service.MemoryBytes <= 1024 * 1024, $"{h.Service.MemoryBytes} bytes in memory");
        Assert.True(h.Sender.BytesHeld(Player) <= TerrainTileSender.QueueTiles * (long)largest);

        Wait.Until(() => h.Sender.TilesSent == keys.Length, () =>
        {
            h.AcknowledgeAll();
            h.Tick();
            Assert.True(h.Service.MemoryBytes <= 1024 * 1024, $"{h.Service.MemoryBytes} bytes in memory");
        }, "every tile sent");

        Assert.Equal(keys.Length * (long)TerrainTileKey.ColumnCount, h.Terrain.Samples);
        Assert.True(h.Service.DiskHits > 0, "nothing was read back from disk");
        Assert.Equal(keys.Length, h.Data.Where(d => d.Offset == 0).Select(d => new TerrainTileKey(d.Step, d.TileX, d.TileZ)).Distinct().Count());
    }

    [Fact]
    public void Without_a_disk_tiles_for_a_slow_client_are_made_only_as_it_has_room()
    {
        using Harness h = new(memoryMegabytes: 1, noisy: true);
        TerrainTileKey[] keys = [.. Enumerable.Range(0, 20).Select(i => Near(i % 5, i / 5))];
        h.Service.Apply(Player, Want(keys));
        Wait.Until(() => h.Sender.TilesSent == TerrainTileProtocol.WindowTiles && h.Sender.Count(Player) == TerrainTileSender.QueueTiles, () => h.Tick(), "the window and the queue full");
        for (int tick = 0; tick < 50; tick++)
            h.Tick();

        // Two sent, two queued, nothing more made for a client that takes nothing.
        Assert.Equal(TerrainTileProtocol.WindowTiles + TerrainTileSender.QueueTiles, h.Service.Generated);
        Wait.Until(() => h.Sender.TilesSent == keys.Length, () =>
        {
            h.AcknowledgeAll();
            h.Tick();
        }, "every tile sent");

        Assert.Equal(keys.Length, h.Service.Generated);
        Assert.True(h.Service.MemoryBytes <= 1024 * 1024, $"{h.Service.MemoryBytes} bytes in memory");
    }

    [Fact]
    public void A_priority_change_for_a_tile_on_its_way_neither_sends_it_again_nor_counts_it()
    {
        using Harness h = new(maxWanted: 3);
        h.MakeInMemory(Near(0), Near(1), Near(2), Near(3));
        h.Service.Apply(Player, Want([Near(0)]));
        h.Tick();
        Assert.Equal(1, h.Sender.TilesSent);

        // Asked for again, sooner, with three more: only those three count, and they fit the limit of three.
        h.Service.Apply(Player, Want([Near(0), Near(1), Near(2), Near(3)]));
        for (int tick = 0; tick < 10; tick++)
            h.Tick();

        Assert.Empty(h.Refusals);
        Assert.Equal([Near(0), Near(1)], h.Data.Where(d => d.Offset == 0).Select(d => new TerrainTileKey(d.Step, d.TileX, d.TileZ)));

        // Dropped while it awaits acknowledgement, and asked for again: the client may have let it go, so it goes again.
        h.Service.Apply(Player, Want([Near(0)], drops: [Near(0)]));
        for (int tick = 0; tick < 20; tick++)
        {
            h.AcknowledgeAll();
            h.Tick();
        }

        Assert.Equal(2, h.Data.Count(d => d.Offset == 0 && d.TileX == Near(0).TileX && d.TileZ == Near(0).TileZ));
        Assert.Empty(h.Refusals);
    }

    [Fact]
    public void Thousands_of_invalid_keys_are_refused_in_one_message()
    {
        using Harness h = new();
        TerrainTileKey[] keys = [.. Enumerable.Range(0, TerrainTileProtocol.MaxKeysPerWant).Select(i => new TerrainTileKey(4, -1 - i, int.MinValue + i))];
        h.Service.Apply(Player, Want(keys));
        h.Tick();

        TerrainTileRefused refused = Protobuf.Wire(Assert.Single(h.Refusals));
        Assert.Contains("outside the world", refused.Reason);
        Assert.Equal(keys, refused.Keys());
        Assert.Equal(0, h.Sender.RefusalsQueued(Player));
    }

    [Fact]
    public void Before_the_service_starts_a_want_is_refused_in_one_message()
    {
        List<TerrainTileRefused> refusals = [];
        TerrainTileSender sender = new((_, _) => { }, (_, refused) => refusals.Add(refused), (_, _) => { });
        TerrainTileKey[] keys = [.. Enumerable.Range(0, TerrainTileProtocol.MaxKeysPerWant).Select(i => new TerrainTileKey(4, i, 0))];
        sender.OnWant(Player, Want(keys), null, "Not started yet.");
        Assert.Empty(refusals);

        sender.Tick(256 * 1024, 0, _ => 0, 0, 0);
        TerrainTileRefused refused = Assert.Single(refusals);
        Assert.Equal("Not started yet.", refused.Reason);
        Assert.Equal(keys.Length, refused.Count);
    }

    [Fact]
    public void A_player_who_may_not_ask_is_looked_up_once_and_refused_in_one_message()
    {
        using Harness h = new();
        h.Owner = TerrainTileOwner.Denied("NetworkSampling is off.");
        int lookups = h.OwnerLookups;
        h.Service.Apply(Player, Want(Enumerable.Range(0, TerrainTileProtocol.MaxKeysPerWant).Select(i => Near(i % 64, i / 64))));
        Assert.Equal(lookups + 1, h.OwnerLookups);

        h.Tick();
        TerrainTileRefused refused = Assert.Single(h.Refusals);
        Assert.Equal("NetworkSampling is off.", refused.Reason);
        Assert.Equal(TerrainTileProtocol.MaxKeysPerWant, refused.Count);
    }

    [Fact]
    public void Refusals_wait_their_turn_under_the_byte_budget()
    {
        List<TerrainTileRefused> refusals = [];
        TerrainTileSender sender = new((_, _) => { }, (_, refused) => refusals.Add(refused), (_, _) => { });
        sender.Refuse(Player, Near(0), "One reason.");
        sender.Refuse(Player, Near(1), "Another.");
        sender.Refuse(Player, Near(2), "One reason.");
        sender.Refuse(Player, Near(2), "One reason.");

        sender.Tick(1, 0, _ => 0, 0, 0);
        TerrainTileRefused first = Assert.Single(refusals);
        Assert.Equal("One reason.", first.Reason);
        Assert.Equal([Near(0), Near(2)], first.Keys());

        sender.Tick(1, 50, _ => 0, 0, 0);
        Assert.Equal(2, refusals.Count);
        Assert.Equal([Near(1)], refusals[1].Keys());
    }

    [Fact]
    public void A_client_cannot_pile_up_refusals_without_bound()
    {
        using Harness h = new();
        for (int message = 0; message < 20; message++)
            h.Service.Apply(Player, Want(Enumerable.Range(0, TerrainTileProtocol.MaxKeysPerWant).Select(i => new TerrainTileKey(3, message, i))));

        Assert.Equal(TerrainTileSender.MaxQueuedRefusals, h.Sender.RefusalsQueued(Player));
        for (int tick = 0; tick < 4; tick++)
            h.Tick();

        Assert.All(h.Refusals, r => Assert.True(r.Count <= TerrainTileProtocol.MaxKeysPerWant));
        Assert.Equal(TerrainTileSender.MaxQueuedRefusals - h.Refusals.Sum(r => r.Count), h.Sender.RefusalsQueued(Player));
    }
}
