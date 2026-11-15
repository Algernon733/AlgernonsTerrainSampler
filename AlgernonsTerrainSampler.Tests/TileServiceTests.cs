using AlgernonsTerrainSampler.Tiles;

namespace AlgernonsTerrainSampler.Tests;

public class TileServiceTests
{
    private const int World = 1024000;
    private static readonly string Identity = TerrainTileIdentity.Hash(writer => writer.Write("test world"));

    private sealed class Harness : IDisposable
    {
        public readonly FakeTerrain Terrain = new();
        public readonly List<(string Owner, TerrainTileKey Key, byte[] Data)> Ready = [];
        public readonly List<(string Owner, TerrainTileKey Key, string Reason)> Refused = [];
        public readonly Dictionary<string, TerrainTileOwner> Owners = [];
        public readonly TerrainTileDiskCache? Disk;
        public readonly TerrainTileService Service;
        public readonly List<string> Log = [];
        public long Now;

        public Harness(string? diskRoot = null, TerrainTileService.Settings? settings = null)
        {
            this.Disk = diskRoot == null ? null : new TerrainTileDiskCache(diskRoot, Identity, 64);
            this.Service = new TerrainTileService(
                World,
                World,
                this.Terrain.Sample,
                this.Disk,
                settings ?? new TerrainTileService.Settings(Workers: 1, MemoryCacheMegabytes: 64, MaxWantedTiles: 1024, MaxDistancePerStep: 4096),
                owner => this.Owners.TryGetValue(owner, out TerrainTileOwner info) ? info : TerrainTileOwner.Unlimited,
                (owner, key, data) => this.Ready.Add((owner, key, data)),
                (owner, key, reason) => this.Refused.Add((owner, key, reason)),
                this.Log.Add,
                () => this.Now);
        }

        public void WaitForReady(int count) => Wait.Until(() => this.Ready.Count >= count, this.Service.Tick, $"{count} tile(s)");

        public void WaitForDisk() => Wait.Until(() => this.Disk == null || this.Disk.Ready, () => { }, "the disk index");

        public void Dispose() => this.Service.Dispose();
    }

    private static TerrainTileKey Near(int dx = 0, int dz = 0) => new(4, 1000 + dx, 1000 + dz);

    [Fact]
    public void A_tile_wanted_by_two_owners_is_made_once_and_sent_to_both()
    {
        using Harness h = new();
        h.Service.Want("a", Near(), 0);
        h.Service.Want("b", Near(), 5);
        h.WaitForReady(2);

        Assert.Equal(TerrainTileKey.ColumnCount, h.Terrain.Samples);
        Assert.Equal(1, h.Service.Generated);
        Assert.Equal(["a", "b"], h.Ready.Select(r => r.Owner).Order());
        Assert.Same(h.Ready[0].Data, h.Ready[1].Data);
        Assert.Equal(FakeTerrain.Column(Near().OriginX, Near().OriginZ).Height, TerrainTileCodec.Decode(h.Ready[0].Data, Near()).Height[0]);
    }

    [Fact]
    public void A_made_tile_is_answered_from_memory_at_once()
    {
        using Harness h = new();
        h.Service.Want("a", Near(), 0);
        h.WaitForReady(1);

        h.Service.Want("b", Near(), 0);
        Assert.Equal(2, h.Ready.Count);
        Assert.Equal(TerrainTileKey.ColumnCount, h.Terrain.Samples);
        Assert.Equal(1, h.Service.MemoryHits);
    }

    [Fact]
    public void A_restarted_server_answers_from_disk_without_sampling()
    {
        using TemporaryFolder folder = new();
        byte[] first;
        using (Harness h = new(folder.Path))
        {
            h.WaitForDisk();
            h.Service.Want("a", Near(), 0);
            h.WaitForReady(1);
            first = h.Ready[0].Data;
        }

        using Harness again = new(folder.Path);
        again.WaitForDisk();
        Assert.Equal(1, again.Disk!.Count);
        again.Service.Want("a", Near(), 0);
        again.WaitForReady(1);

        Assert.Equal(0, again.Terrain.Samples);
        Assert.Equal(1, again.Service.DiskHits);
        Assert.Equal(first, again.Ready[0].Data);
    }

    [Fact]
    public void A_damaged_file_on_disk_is_sampled_again()
    {
        using TemporaryFolder folder = new();
        string? path;
        using (Harness h = new(folder.Path))
        {
            h.WaitForDisk();
            h.Service.Want("a", Near(), 0);
            h.WaitForReady(1);
            path = Directory.EnumerateFiles(h.Disk!.Folder, "*.ats").Single();
        }

        byte[] bytes = File.ReadAllBytes(path);
        bytes[^5] ^= 0x55;
        File.WriteAllBytes(path, bytes);

        using Harness again = new(folder.Path);
        again.WaitForDisk();
        again.Service.Want("a", Near(), 0);
        again.WaitForReady(1);

        Assert.Equal(TerrainTileKey.ColumnCount, again.Terrain.Samples);
        Assert.True(TerrainTileCodec.Verify(again.Ready[0].Data, Near()));
        Assert.True(TerrainTileCodec.Verify(File.ReadAllBytes(path), Near()));
    }

    [Fact]
    public void A_failure_is_refused_and_never_kept()
    {
        using TemporaryFolder folder = new();
        using Harness h = new(folder.Path);
        h.WaitForDisk();
        h.Terrain.FailIn = Near();
        h.Service.Want("a", Near(), 0);
        Wait.Until(() => h.Refused.Count == 1, h.Service.Tick, "the refusal");

        Assert.Contains("broken generator", h.Refused[0].Reason);
        Assert.Equal(0, h.Disk!.Count);
        Assert.Equal(0, h.Service.MemoryTiles);

        h.Terrain.FailIn = null;
        h.Service.Want("a", Near(), 0);
        h.WaitForReady(1);
        Assert.Equal(1, h.Service.Generated);
        Wait.Until(() => h.Disk.Count == 1, () => { }, "the disk write");
    }

    [Fact]
    public void A_broken_sampler_logs_its_trace_once_turns_the_service_off_and_comes_back()
    {
        using Harness h = new();
        h.Terrain.FailAll = true;
        for (int i = 0; i < 12; i++)
            h.Service.Want("a", Near(i), i);

        Wait.Until(() => h.Refused.Count == 12, h.Service.Tick, "every tile refused");
        Assert.NotNull(h.Service.FailureReason);
        Assert.Contains($"{TerrainTileService.BrokenAfterFailures} tiles in a row", h.Service.FailureReason);
        Assert.Equal(TerrainTileService.BrokenAfterFailures, h.Service.Failures);
        Assert.Single(h.Log, line => line.Contains("broken generator") && line.Contains("   at "));
        Assert.All(h.Refused.Skip(TerrainTileService.BrokenAfterFailures), r => Assert.Equal(h.Service.FailureReason, r.Reason));

        // While it is off, a want is refused at once, and nothing is sampled.
        long sampled = h.Terrain.Samples;
        h.Service.Want("b", Near(20), 0);
        Assert.Equal(h.Service.FailureReason, h.Refused[^1].Reason);
        for (int tick = 0; tick < 20; tick++)
            h.Service.Tick();

        Assert.Equal(sampled, h.Terrain.Samples);

        // A minute on, the repeats are summed up and the failed tile is tried again; the sampler works, so tiles do.
        h.Terrain.FailAll = false;
        h.Now += TerrainTileService.RetryMilliseconds;
        Wait.Until(() => h.Service.FailureReason == null, h.Service.Tick, "the service to come back");
        Assert.Contains(h.Log, line => line.Contains($"the same failure {TerrainTileService.BrokenAfterFailures - 1} more time(s)"));
        Assert.Contains(h.Log, line => line.Contains("works again"));

        h.Service.Want("b", Near(21), 0);
        h.WaitForReady(1);
        Assert.Equal(Near(21), h.Ready.Single(r => r.Owner == "b").Key);
    }

    [Fact]
    public void A_sampler_still_broken_stays_off_and_is_checked_once_a_minute()
    {
        using Harness h = new();
        h.Terrain.FailAll = true;
        for (int i = 0; i < TerrainTileService.BrokenAfterFailures; i++)
            h.Service.Want("a", Near(i), i);

        Wait.Until(() => h.Service.FailureReason != null, h.Service.Tick, "the service to go off");
        long sampled = h.Terrain.Samples;
        h.Service.Tick();
        Assert.Equal(sampled, h.Terrain.Samples);

        // A minute on, one column is checked, and fails; nothing more until the minute after.
        h.Now += TerrainTileService.RetryMilliseconds;
        for (int tick = 0; tick < 20; tick++)
            h.Service.Tick();

        Assert.Equal(sampled + 1, h.Terrain.Samples);
        Assert.NotNull(h.Service.FailureReason);
        Assert.Equal(TerrainTileService.BrokenAfterFailures, h.Service.Failures);
        Assert.Single(h.Log, line => line.Contains("   at "));
    }

    [Fact]
    public void An_area_that_cannot_be_sampled_is_refused_on_its_own_and_never_turns_the_service_off()
    {
        // East of 600 km nothing can be sampled, as past a world edge or where a modded generator throws.
        using Harness h = new();
        h.Terrain.FailWhere = (x, _) => x >= 600000;
        TerrainTileKey Bad(int i) => new(4, (600000 / 512) + 1 + (i % 6), 1000 + (i / 6));
        for (int i = 0; i < TerrainTileService.BrokenAfterFailures * 3; i++)
            h.Service.Want("a", Bad(i), i);

        Wait.Until(() => h.Refused.Count == TerrainTileService.BrokenAfterFailures * 3, h.Service.Tick, "every bad tile refused");
        Assert.Null(h.Service.FailureReason);
        Assert.All(h.Refused, r => Assert.Contains("failed on this tile", r.Reason));
        Assert.Contains(h.Log, line => line.Contains("but the sampler still works"));

        h.Service.Want("b", Near(), 0);
        h.WaitForReady(1);

        // A sampler that breaks for good still turns it off, checked where that tile was made.
        h.Terrain.FailAll = true;
        for (int i = 0; i < TerrainTileService.BrokenAfterFailures; i++)
            h.Service.Want("c", Near(i + 1), i);

        Wait.Until(() => h.Service.FailureReason != null, h.Service.Tick, "the service to go off");
        h.Terrain.FailAll = false;
        h.Now += TerrainTileService.RetryMilliseconds;
        Wait.Until(() => h.Service.FailureReason == null, h.Service.Tick, "the service to come back");
    }

    [Fact]
    public void Failures_between_good_tiles_never_turn_the_service_off()
    {
        using Harness h = new();
        for (int i = 0; i < TerrainTileService.BrokenAfterFailures * 2; i++)
        {
            h.Terrain.FailIn = i % 2 == 0 ? Near(i) : null;
            h.Service.Want("a", Near(i), 0);
            Wait.Until(() => h.Ready.Count + h.Refused.Count == i + 1, h.Service.Tick, "the tile");
        }

        Assert.Null(h.Service.FailureReason);
        Assert.Equal(TerrainTileService.BrokenAfterFailures, h.Refused.Count);
    }

    [Fact]
    public void Requests_outside_the_rules_are_refused_with_a_reason()
    {
        using Harness h = new(settings: new TerrainTileService.Settings(1, 64, MaxWantedTiles: 2, MaxDistancePerStep: 4096));
        TerrainTileKey here = TerrainTileKey.Containing(4, 512000, 512000);
        h.Owners["player"] = new TerrainTileOwner(true, "", true, 512000, 512000);
        h.Owners["banned"] = TerrainTileOwner.Denied("No tiles for you.");

        h.Service.Want("player", new TerrainTileKey(3, 0, 0), 0);
        h.Service.Want("banned", here, 0);
        h.Service.Want("player", new TerrainTileKey(4, here.TileX + 40, here.TileZ), 0); // 20 km away at step 4
        h.Service.Want("player", here, 0);
        h.Service.Want("player", new TerrainTileKey(4, here.TileX + 1, here.TileZ), 0);
        h.Service.Want("player", new TerrainTileKey(4, here.TileX + 2, here.TileZ), 0); // a third at once

        Assert.Equal(4, h.Refused.Count);
        Assert.Contains("power of two", h.Refused[0].Reason);
        Assert.Equal("No tiles for you.", h.Refused[1].Reason);
        Assert.Contains("16384 blocks", h.Refused[2].Reason);
        Assert.Contains("More than 2", h.Refused[3].Reason);

        h.WaitForReady(2);
        Assert.DoesNotContain(h.Ready, r => r.Owner == "banned");
    }

    [Fact]
    public void No_step_is_served_past_the_absolute_distance()
    {
        // At step 64 the limit for the step would be 524 km; the absolute limit is 32 km.
        using Harness h = new(settings: new TerrainTileService.Settings(1, 64, 1024, MaxDistancePerStep: 8192, MaxDistance: 32768));
        h.Owners["player"] = new TerrainTileOwner(true, "", true, 512000, 512000);
        TerrainTileKey near = TerrainTileKey.Containing(64, 512000 + 16000, 512000);
        TerrainTileKey far = TerrainTileKey.Containing(64, 512000 + 40000, 512000);
        h.Service.Want("player", far, 0);
        h.Service.Want("player", near, 0);
        h.Service.Want("mod", far, 0);

        Assert.Equal(far, Assert.Single(h.Refused).Key);
        Assert.Contains("within 32768 blocks", h.Refused[0].Reason);
        h.WaitForReady(2);
    }

    [Fact]
    public void A_player_who_moves_away_loses_tiles_that_are_now_too_far()
    {
        using Harness h = new(settings: new TerrainTileService.Settings(1, 64, 1024, MaxDistancePerStep: 4096), diskRoot: null);
        TerrainTileKey here = TerrainTileKey.Containing(1, 512000, 512000);
        h.Owners["player"] = new TerrainTileOwner(true, "", true, 512000, 512000);
        for (int i = 0; i < 8; i++)
            h.Service.Want("player", new TerrainTileKey(1, here.TileX + i, here.TileZ), i);

        h.Owners["player"] = new TerrainTileOwner(true, "", true, 600000, 600000);
        Wait.Until(() => h.Refused.Count + h.Ready.Count == 8, h.Service.Tick, "every tile answered");
        Assert.NotEmpty(h.Refused);
        Assert.All(h.Refused, r => Assert.Contains("4096 blocks", r.Reason));
    }

    [Fact]
    public void A_tile_whose_owner_left_is_finished_and_kept()
    {
        using Harness h = new();
        h.Service.Want("a", Near(), 0);
        h.Service.Tick();
        h.Service.DropOwner("a");
        Wait.Until(() => h.Service.Running == 0 && h.Service.MemoryTiles == 1, h.Service.Tick, "the tile to be finished");

        Assert.Empty(h.Ready);
        h.Service.Want("b", Near(), 0);
        Assert.Single(h.Ready);
        Assert.Equal(TerrainTileKey.ColumnCount, h.Terrain.Samples);
    }

    [Fact]
    public void Owners_take_turns_and_each_owner_gets_its_most_urgent_first()
    {
        using Harness h = new();
        for (int i = 0; i < 4; i++)
            h.Service.Want("busy", Near(i), 10 - i);

        h.Service.Want("quiet", Near(0, 9), 0);
        h.WaitForReady(5);

        List<TerrainTileKey> order = [.. h.Ready.Select(r => r.Key)];
        Assert.Contains(Near(0, 9), order.Take(2));
        List<TerrainTileKey> busy = [.. order.Where(k => k != Near(0, 9))];
        Assert.Equal([Near(3), Near(2), Near(1), Near(0)], busy);
    }

    [Fact]
    public void Sampling_on_the_main_thread_makes_the_same_tile()
    {
        using Harness workers = new();
        workers.Service.Want("a", Near(), 0);
        workers.WaitForReady(1);

        using Harness main = new(settings: new TerrainTileService.Settings(1, 64, 1024, 4096, SampleOnMainThread: true, MainThreadBudgetMilliseconds: 1));
        main.Service.Want("a", Near(), 0);
        main.WaitForReady(1);

        Assert.Equal(workers.Ready[0].Data, main.Ready[0].Data);
    }

    [Fact]
    public void Memory_holds_no_more_than_its_budget()
    {
        using Harness h = new(settings: new TerrainTileService.Settings(2, MemoryCacheMegabytes: 1, 4096, 4096));

        // The fake terrain compresses well, so keep asking until well over a mebibyte has been made.
        int asked = 0;
        while (h.Ready.Sum(r => (long)r.Data.Length) < 3 * 512 * 1024 && asked < 2000)
        {
            for (int i = 0; i < 32; i++, asked++)
                h.Service.Want("a", Near(asked % 40, asked / 40), asked);

            h.WaitForReady(asked);
        }

        h.Service.Tick();
        Assert.True(h.Service.MemoryBytes <= 1024 * 1024, $"{h.Service.MemoryBytes} bytes in memory");
        Assert.True(h.Service.MemoryTiles < asked, $"all {asked} tiles kept");
    }

    [Theory]
    [InlineData(1024, 15)]
    [InlineData(961, 15)]
    [InlineData(960, 14)]
    [InlineData(9, 1)]
    [InlineData(8, 0)]
    [InlineData(1, 0)]
    [InlineData(65536, 127)]
    public void The_server_fetch_square_fits_the_allowance_server_mods_share(int maxWanted, int radius)
    {
        Assert.Equal(radius, TerrainTileConfig.SquareRadiusWithin(maxWanted));
        int side = (2 * radius) + 1;
        Assert.True(side * side <= maxWanted);
        Assert.True((side + 2) * (side + 2) > maxWanted);
    }

    [Fact]
    public void Server_mods_asking_for_the_widest_square_at_the_default_limit_are_never_refused()
    {
        using Harness h = new();
        int radius = TerrainTileConfig.SquareRadiusWithin(new TerrainTileConfig().MaxWantedTiles);
        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
                h.Service.Want("mods", Near(dx + 20, dz + 20), 0);
        }

        Assert.Empty(h.Refused);
        Assert.Equal(((2 * radius) + 1) * ((2 * radius) + 1), h.Service.WantedBy("mods"));
    }

    [Fact]
    public void A_config_without_the_absolute_distance_gets_its_default_and_a_wild_one_is_clamped()
    {
        TerrainTileConfig config = new();
        Assert.Equal(32768, config.MaxDistance);
        config.MaxDistance = -5;
        config.Clamp();
        Assert.Equal(128, config.MaxDistance);
    }

    [Fact]
    public void A_want_message_resets_drops_and_adds()
    {
        using Harness h = new();
        h.Service.Want("a", Near(0), 0);
        h.Service.Want("a", Near(1), 0);
        h.Service.Apply("a", new TerrainTileWant
        {
            Reset = true,
            AddStep = [4, 4],
            AddX = [Near(2).TileX, Near(3).TileX],
            AddZ = [Near(2).TileZ, Near(3).TileZ],
            AddPriority = [1, 0],
            DropStep = [4],
            DropX = [Near(3).TileX],
            DropZ = [Near(3).TileZ]
        });

        Assert.Equal(2, h.Service.WantedBy("a"));
        h.WaitForReady(2);
        Assert.Equal([Near(3), Near(2)], h.Ready.Select(r => r.Key));
    }
}
