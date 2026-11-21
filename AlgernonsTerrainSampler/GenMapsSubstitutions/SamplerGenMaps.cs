#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.ServerMods;

namespace AlgernonsTerrainSampler.GenMapsSubstitutions;

/// <summary>
/// The map generators the sampler reads (climate, upheaval, ocean, forest, shrub and landforms) and the top rock
/// sampler. Each generator is the sampler's own copy, made from the seed and the world configuration as vanilla's
/// <c>GenMaps.initWorldGen</c> makes the game's, parameter for parameter, unless a mod has replaced the game's: then the
/// game's is followed, as before 2.0.0.
/// </summary>
/// <remarks>
/// <para>
/// Why copies: vanilla's <c>GenMaps.OnMapRegionGen</c> uses the game's generators on the game's worldgen thread without a
/// lock. It calls <c>SetInputMap</c> and then <c>GenLayer</c> on the forest and shrub layers, its climate and landform
/// layers share one climate noise that keeps a position seed between draws, and its landform layer keeps its weights in
/// the game's static landforms. A sampler thread using any of that, even under a lock of its own, could change both its
/// own answers and the maps the game saves. The landform copy is therefore the sampler's own port of the layer
/// (<see cref="SamplerLandformLayer"/>), over the sampler's own landforms.
/// </para>
/// <para>
/// Why follow a replaced one: the sampler is meant to answer as the world generates, and a terrain mod may put its own
/// generator in place of vanilla's. Each of the game's generators and its copy generate the same test map regions; a
/// generator whose type or output differs is the game's from then on. This is done when the world generator starts and
/// again, finally, when the game starts running (<see cref="Settle"/>): after every mod's world generator has started and
/// before the game's chunk threads and the tile service, so the game's generators are safe to call and a sample never
/// mixes two generators. Using a followed one is not thread safe, as it never was, and the log says so. A mod whose
/// changes miss the test regions goes unnoticed; <c>/terrainsampler mapcheck</c> compares the sampler's maps with the
/// world's own anywhere.
/// </para>
/// <para>
/// When: after <c>GenMaps</c>' own start (this system's <see cref="ExecuteOrder"/> is later than its, and the game runs
/// those handlers in order). The ocean layer reads the spawn point <c>GenMaps</c> forces to land, and the upheaval layer
/// the commonness <c>GenMaps</c> reads from the world configuration. Making the geologic province layer for the top rock
/// reloads vanilla's static provinces, which is only safe before any chunk is generated, as vanilla does it. A world
/// generator started again while the game runs (<c>/wgen regen</c>) makes a new set and puts it in place whole, without
/// clearing the one samples in flight are using.
/// </para>
/// </remarks>
public sealed class SamplerGenMaps : ModSystem
{
    private enum Layer
    {
        Climate,
        Upheaval,
        Ocean,
        Forest,
        Shrub,
        Landforms,
    }

    private static readonly Layer[] Layers = Enum.GetValues<Layer>();

    /// <summary>One world's generators, made whole and then put in place: nothing reads a set half made.</summary>
    private sealed class GeneratorSet
    {
        public static readonly GeneratorSet None = new(null, new MapLayerBase?[Layers.Length], new MapLayerBase?[Layers.Length], new bool[Layers.Length]);

        public GeneratorSet(GenMaps? gameMaps, MapLayerBase?[] own, MapLayerBase?[] chosen, bool[] followsGame)
        {
            this.GameMaps = gameMaps;
            this.Own = own;
            this.Chosen = chosen;
            this.FollowsGame = followsGame;
        }

        public GenMaps? GameMaps { get; }

        public MapLayerBase?[] Own { get; }

        public MapLayerBase?[] Chosen { get; }

        public bool[] FollowsGame { get; }
    }

    private ICoreServerAPI? api;
    private volatile GeneratorSet current = GeneratorSet.None;

    // The climate and landform layers share one climate noise, the sampler's copies theirs and the game's theirs, so each
    // pair shares a lock.
    private readonly object ownClimateNoiseLock = new();
    private readonly object gameClimateNoiseLock = new();

    public MapLayerBase? ClimateGen => this.current.Chosen[(int)Layer.Climate];

    public MapLayerBase? UpheavalGen => this.current.Chosen[(int)Layer.Upheaval];

    public MapLayerBase? OceanGen => this.current.Chosen[(int)Layer.Ocean];

    public MapLayerBase? ForestGen => this.current.Chosen[(int)Layer.Forest];

    public MapLayerBase? ShrubGen => this.current.Chosen[(int)Layer.Shrub];

    public MapLayerBase? LandformsGen => this.current.Chosen[(int)Layer.Landforms];

    /// <summary>The lock for <see cref="ClimateGen"/>, which shares its climate noise with <see cref="LandformsGen"/>.</summary>
    public object ClimateLock => this.current.FollowsGame[(int)Layer.Climate] ? this.gameClimateNoiseLock : this.ownClimateNoiseLock;

    /// <summary>The lock for <see cref="LandformsGen"/>, which shares its climate noise with <see cref="ClimateGen"/>.</summary>
    public object LandformsLock => this.current.FollowsGame[(int)Layer.Landforms] ? this.gameClimateNoiseLock : this.ownClimateNoiseLock;

    /// <summary>Vanilla's rock strata for any column, or null when there are none (<see cref="NoRocksReason"/> says why).</summary>
    public RockStrataSampler? Rocks { get; private set; }

    public string NoRocksReason { get; private set; } = "the world generator has not started";

    /// <summary>Whether this world's generators are there, so that sampling can give real answers.</summary>
    public bool IsReady => this.current.Chosen.All(layer => layer != null);

    /// <summary>Which generators are the sampler's own copies and which the game's, for the log and <c>mapcheck</c>.</summary>
    public string Describe()
    {
        GeneratorSet set = this.current;
        if (!set.Chosen.All(layer => layer != null))
            return "The sampler has no map generators for this world.";

        string[] followed = [.. Layers.Where(layer => set.FollowsGame[(int)layer]).Select(Name)];
        return followed.Length == 0
            ? "Every map generator is the sampler's own copy of vanilla's."
            : $"Following the game's {string.Join(", ", followed)} generator{(followed.Length > 1 ? "s" : "")}, which a mod has changed (not thread safe); the rest are the sampler's own copies.";
    }

    /// <summary>
    /// What the generators are, for the tile cache's identity: which are followed, and their types. A mod that changes
    /// what the game's generators answer changes it.
    /// </summary>
    public string Fingerprint()
    {
        GeneratorSet set = this.current;
        return string.Join(";", Layers.Select(layer => $"{Name(layer)}:{(set.FollowsGame[(int)layer] ? "game" : "own")}:{set.Chosen[(int)layer]?.GetType().FullName ?? "none"}"));
    }

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    // After vanilla's GenMaps (0.1), whose world generator start must have run first.
    public override double ExecuteOrder() => 0.15;

    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        api.Event.InitWorldGenerator(this.InitWorldGen, "standard");
    }

    private void InitWorldGen()
    {
        ICoreServerAPI api = this.api!;
        GeneratorSet set;
        try
        {
            GenMaps gameMaps = api.ModLoader.GetModSystem<GenMaps>()
                ?? throw new InvalidOperationException("vanilla's GenMaps is not loaded");
            MapLayerBase?[] own = this.MakeCopies(api, gameMaps);
            set = this.Choose(api, gameMaps, own, log: false);
        }
        catch (Exception ex)
        {
            this.current = GeneratorSet.None;
            api.Logger.Error("AlgernonsTerrainSampler: the sampler's map generators could not be made, so it cannot sample this world: {0}", ex);
            return;
        }

        this.current = set;

        RockStrataSampler? rocks;
        string reason;
        try
        {
            rocks = RockStrataSampler.TryCreate(api, out reason);
        }
        catch (Exception ex)
        {
            rocks = null;
            reason = ex.Message;
            api.Logger.Warning("AlgernonsTerrainSampler: no top rock in column samples, the rock strata could not be read: {0}", ex);
        }

        this.Rocks = rocks;
        this.NoRocksReason = reason;
        if (rocks == null)
            api.Logger.Notification("AlgernonsTerrainSampler: no top rock in column samples: {0}", reason);
    }

    /// <summary>
    /// The final comparison, when the game starts running: after every mod's world generator has started, before the
    /// game's chunk threads and before anything samples in earnest. Called once, from <see cref="TerrainSamplerMod"/>.
    /// </summary>
    public void Settle()
    {
        ICoreServerAPI? api = this.api;
        GeneratorSet set = this.current;
        if (api == null || set.GameMaps == null)
            return;

        this.current = this.Choose(api, set.GameMaps, set.Own, log: true);
    }

    // GenMaps.initWorldGen, for the layers the sampler reads. A copy that cannot be made (a mod's change to vanilla's
    // generators may not allow it) is left out, and the game's generator is followed for that layer instead.
    private MapLayerBase?[] MakeCopies(ICoreServerAPI api, GenMaps gameMaps)
    {
        MapLayerBase?[] own = new MapLayerBase?[Layers.Length];
        long seed = WorldgenSeed.Of(api.WorldManager);
        ITreeAttribute config = api.WorldManager.SaveGame.WorldConfiguration;
        float landcover = StringUtil.ToFloat(config.GetString("landcover", "1"), 1f);
        float oceanScaleMul = StringUtil.ToFloat(config.GetString("oceanscale", "1"), 1f);
        float landformScale = StringUtil.ToFloat(config.GetString("landformScale", "1.0"), 1f);
        bool requiresSpawnOffset = GameVersion.IsLowerVersionThan(api.WorldManager.SaveGame.CreatedGameVersion, "1.20.0-pre.14");
        NoiseClimate climateNoise = MakeClimateNoise(api, seed, config);

        void Make(Layer layer, Func<MapLayerBase> make)
        {
            try
            {
                own[(int)layer] = make();
            }
            catch (Exception ex)
            {
                own[(int)layer] = null;
                api.Logger.Warning("AlgernonsTerrainSampler: the sampler's copy of the {0} map generator could not be made: {1}", Name(layer), ex);
            }
        }

        Make(Layer.Climate, () => GenMaps.GetClimateMapGen(seed + 1, climateNoise));

        // GenMaps.upheavelCommonness is the world configuration's, read by GenMaps.initWorldGen just before.
        Make(Layer.Upheaval, () => GenMaps.GetGeoUpheavelMapGen(seed + 873, TerraGenConfig.geoUpheavelMapScale));

        // The game's own list of places forced to land: GenMaps and story structures fill it as the world generator
        // starts, and the ocean layer reads it as it generates.
        Make(Layer.Ocean, () => gameMaps.requireLandAt.Count == 0
            ? throw new InvalidOperationException("vanilla's GenMaps forced no land at spawn")
            : GenMaps.GetOceanMapGen(seed + 1873, landcover, TerraGenConfig.oceanMapScale, oceanScaleMul, gameMaps.requireLandAt, requiresSpawnOffset));
        Make(Layer.Forest, () => GenMaps.GetForestMapGen(seed + 2, TerraGenConfig.forestMapScale));
        Make(Layer.Shrub, () => GenMaps.GetForestMapGen(seed + 109, TerraGenConfig.shrubMapScale));

        // GenMaps.GetLandformMapGen(seed + 4, ...) makes a MapLayerLandforms of seed + 4 + 12.
        Make(Layer.Landforms, () => new SamplerLandformLayer(
            seed + 4 + 12,
            climateNoise,
            api,
            landformScale,
            NoiseLandforms.LandformsProperty ?? throw new InvalidOperationException("the sampler's landforms are not loaded")));
        return own;
    }

    // Each of the game's generators against its copy, on the same test regions. Safe to call the game's here: the game
    // runs world generator starts one at a time, and its chunk threads start only after the run phase that settles this.
    private GeneratorSet Choose(ICoreServerAPI api, GenMaps gameMaps, MapLayerBase?[] own, bool log)
    {
        MapLayerBase?[] chosen = new MapLayerBase?[Layers.Length];
        bool[] followsGame = new bool[Layers.Length];
        IWorldManagerAPI world = api.WorldManager;
        int spawnRegionX = world.MapSizeX / 2 / world.RegionSize;
        int spawnRegionZ = world.MapSizeZ / 2 / world.RegionSize;
        (int X, int Z)[] regions = [(spawnRegionX, spawnRegionZ), (spawnRegionX + 37, Math.Max(0, spawnRegionZ - 23))];

        void Follow(Layer layer, MapLayerBase theirs, string why)
        {
            chosen[(int)layer] = theirs;
            followsGame[(int)layer] = true;
            if (log)
            {
                api.Logger.Warning(
                    "AlgernonsTerrainSampler: the game's {0} map generator {1}, so the sampler follows it as the world does. It is "
                    + "the same instance the game's world generation uses, so sampling from other threads is not safe while "
                    + "chunks are generated there.", Name(layer), why);
            }
        }

        foreach (Layer layer in Layers)
        {
            MapLayerBase? copy = own[(int)layer];
            MapLayerBase? theirs = GameLayer(gameMaps, layer);
            chosen[(int)layer] = copy;
            if (theirs == null)
                continue;

            if (copy == null)
            {
                Follow(layer, theirs, "could not be copied");
                continue;
            }

            Type vanilla = layer == Layer.Landforms ? typeof(MapLayerLandforms) : copy.GetType();
            if (theirs.GetType() != vanilla)
            {
                Follow(layer, theirs, $"is a {theirs.GetType().FullName}, not vanilla's {vanilla.FullName}");
                continue;
            }

            bool same;
            try
            {
                // The forest and shrub layers are given the climate map of the climate generator chosen just before.
                MapLayerBase climate = chosen[(int)Layer.Climate]!;
                same = regions.All(region => Generate(world.RegionSize, layer, theirs, climate, region).AsSpan()
                    .SequenceEqual(Generate(world.RegionSize, layer, copy, climate, region)));
            }
            catch (Exception ex)
            {
                // Vanilla's own type, failing on a test region: keep the copy, which is vanilla's.
                if (log)
                    api.Logger.Warning("AlgernonsTerrainSampler: the game's {0} map generator could not be compared with the sampler's copy, which is used: {1}", Name(layer), ex);
                continue;
            }

            if (!same)
                Follow(layer, theirs, "generates other maps than vanilla's");
        }

        return new GeneratorSet(gameMaps, own, chosen, followsGame);
    }

    // A test map region from a generator, sized and padded as GenMaps.OnMapRegionGen makes it. The forest and shrub layers
    // are given the same climate map whichever generator it is.
    private static int[] Generate(int regionSize, Layer layer, MapLayerBase generator, MapLayerBase climateGenerator, (int X, int Z) region)
    {
        (int scale, int padding) = layer switch
        {
            Layer.Climate => (TerraGenConfig.climateMapScale, 2),
            Layer.Upheaval => (TerraGenConfig.climateMapScale, 3),
            Layer.Ocean => (TerraGenConfig.oceanMapScale, 5),
            Layer.Landforms => (TerraGenConfig.landformMapScale, TerraGenConfig.landformMapPadding),
            Layer.Forest => (TerraGenConfig.forestMapScale, 0),
            _ => (TerraGenConfig.shrubMapScale, 0),
        };

        int size = regionSize / scale;
        if (layer is not (Layer.Forest or Layer.Shrub))
            return generator.GenLayer((region.X * size) - padding, (region.Z * size) - padding, size + (2 * padding), size + (2 * padding));

        int climateSize = regionSize / TerraGenConfig.climateMapScale;
        IntDataMap2D climate = new()
        {
            Data = climateGenerator.GenLayer((region.X * climateSize) - 2, (region.Z * climateSize) - 2, climateSize + 4, climateSize + 4),
            Size = climateSize + 4,
            TopLeftPadding = 2,
            BottomRightPadding = 2,
        };
        IntDataMap2D output = new() { Size = size + 1, BottomRightPadding = 1 };
        generator.SetInputMap(climate, output);
        return generator.GenLayer(region.X * size, region.Z * size, size + 1, size + 1);
    }

    private static MapLayerBase? GameLayer(GenMaps maps, Layer layer) => layer switch
    {
        Layer.Climate => maps.climateGen,
        Layer.Upheaval => maps.upheavelGen,
        Layer.Ocean => maps.oceanGen,
        Layer.Forest => maps.forestGen,
        Layer.Shrub => maps.bushGen,
        _ => maps.landformsGen,
    };

    private static string Name(Layer layer) => layer == Layer.Landforms ? "landform" : layer.ToString().ToLowerInvariant();

    private static NoiseClimate MakeClimateNoise(ICoreServerAPI api, long seed, ITreeAttribute config)
    {
        NoiseClimate noise;
        if (config.GetString("worldClimate", "realistic") == "realistic")
        {
            (int spawnMinTemp, int spawnMaxTemp) = config.GetString("startingClimate", null) switch
            {
                "hot" => (28, 32),
                "warm" => (19, 23),
                "cool" => (-5, 1),
                "icy" => (-15, -10),
                _ => (6, 14),
            };

            int polarEquatorDistance = StringUtil.ToInt(config.GetString("polarEquatorDistance", "50000"), 50000);
            double mapSizeZ = (double)api.WorldManager.MapSizeZ / TerraGenConfig.climateMapScale / TerraGenConfig.climateMapSubScale;
            noise = new NoiseClimateRealistic(seed, mapSizeZ, polarEquatorDistance, spawnMinTemp, spawnMaxTemp)
            {
                GeologicActivityStrength = StringUtil.ToFloat(config.GetString("geologicActivity", null), 0.05f),
            };
        }
        else
        {
            noise = new NoiseClimatePatchy(seed);
        }

        noise.rainMul = StringUtil.ToFloat(config.GetString("globalPrecipitation", "1"), 1f);
        noise.tempMul = StringUtil.ToFloat(config.GetString("globalTemperature", "1"), 1f);
        return noise;
    }

    public override void Dispose()
    {
        this.current = GeneratorSet.None;
        this.Rocks = null;
        this.api = null;
        base.Dispose();
    }
}
