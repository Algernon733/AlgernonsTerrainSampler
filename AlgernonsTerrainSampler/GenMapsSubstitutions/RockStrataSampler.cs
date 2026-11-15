#nullable enable
using System;
using System.Threading;
using AlgernonsTerrainSampler.Terrain;
using Microsoft.Extensions.Caching.Memory;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace AlgernonsTerrainSampler.GenMapsSubstitutions;

/// <summary>
/// The top rock of any column, generated or not: what vanilla's <c>GenRockStrataNew</c> puts at the column's top block,
/// which is the rock its block layers stand on (sand, gravel and bare rock take their type from it).
/// </summary>
/// <remarks>
/// <para>
/// Everything vanilla reads is seeded, so it is rebuilt here for any map region: the region's geologic provinces
/// (<c>GenMaps.GetGeologicProvinceMapGen</c>, seed + 3, smoothed into province weights as vanilla smooths them), each
/// stratum's noise map (<c>MapLayerCustomPerlin</c>, seed + 23423 + its index, from <c>worldgen/rockstrata.json</c> through
/// the asset system, so a mod's patches to it count) and the two distortion noises. They are this class's own instances,
/// not the game's: a map layer keeps a random state, and the game's own worldgen thread uses its instances meanwhile.
/// </para>
/// <para>
/// A world generator other than vanilla's strata (a mod replacing <c>GenRockStrataNew</c>) gets vanilla's answer here; an
/// adapter for it belongs in this class, so that consumers of the sampler need not know.
/// </para>
/// </remarks>
public sealed class RockStrataSampler
{
    // Regions whose maps are kept. Each is about 100 KB (twenty-odd strata maps of 36 x 36 and the province weights).
    private const int RegionsKept = 96;

    private readonly RockStrataConfig strata;
    private readonly RockStratumShape[] shapes;
    private readonly string[] codes;
    private readonly string defaultCode;
    private readonly MapLayerCustomPerlin[] strataNoises;
    private readonly MapLayerBase provinceGen;
    private readonly GeologicProvinces provinces;
    private readonly SimplexNoise distortX;
    private readonly SimplexNoise distortZ;
    private readonly int regionSize;
    private readonly int regionChunkSize;
    private readonly int seaLevel;
    private readonly MemoryCache regions = new(new MemoryCacheOptions { SizeLimit = RegionsKept, CompactionPercentage = 0.25 });
    private readonly object[] regionLocks = new object[64];

    [ThreadStatic]
    private static float[]? weightScratch;

    private RockStrataSampler(ICoreServerAPI api, RockStrataConfig strata, int defaultRockId)
    {
        this.strata = strata;
        this.regionSize = api.WorldManager.RegionSize;
        this.regionChunkSize = this.regionSize / Mapping.CHUNK_SIZE;
        this.seaLevel = TerraGenConfig.seaLevel;
        for (int i = 0; i < this.regionLocks.Length; i++)
            this.regionLocks[i] = new object();

        int mapSizeY = api.WorldManager.MapSizeY;
        this.shapes = new RockStratumShape[strata.Variants.Length];
        this.codes = new string[strata.Variants.Length];
        this.strataNoises = new MapLayerCustomPerlin[strata.Variants.Length];
        for (int i = 0; i < strata.Variants.Length; i++)
        {
            RockStratum stratum = strata.Variants[i];
            this.shapes[i] = new RockStratumShape(
                stratum.GenDir == EnumStratumGenDir.TopDown,
                (int)stratum.RockGroup,
                stratum.RockGroup == EnumRockGroup.Sedimentary,
                stratum.BlockId == defaultRockId);
            this.codes[i] = CodeOf(api, stratum.BlockId);

            // GenRockStrataNew.initWorldGen, parameter for parameter.
            double[] amplitudes = (double[])stratum.Amplitudes.Clone();
            double[] frequencies = (double[])stratum.Frequencies.Clone();
            double[] thresholds = (double[])stratum.Thresholds.Clone();
            for (int k = 0; k < frequencies.Length; k++)
            {
                frequencies[k] /= TerraGenConfig.rockStrataOctaveScale;
                amplitudes[k] *= mapSizeY;
                thresholds[k] *= mapSizeY;
            }

            this.strataNoises[i] = new MapLayerCustomPerlin(api.World.Seed + 23423 + i, amplitudes, frequencies, thresholds);
        }

        this.defaultCode = CodeOf(api, defaultRockId);
        this.provinceGen = GenMaps.GetGeologicProvinceMapGen(api.WorldManager.Seed + 3, api);
        this.provinces = NoiseGeoProvince.provinces;
        this.distortX = new SimplexNoise([14.0, 9.0, 6.0, 3.0], [0.01, 0.02, 0.04, 0.08], api.World.SeaLevel + 9876);
        this.distortZ = new SimplexNoise([14.0, 9.0, 6.0, 3.0], [0.01, 0.02, 0.04, 0.08], api.World.SeaLevel + 9877);
    }

    /// <summary>The rock strata of a world vanilla's <c>GenRockStrataNew</c> generates, or null and why not.</summary>
    public static RockStrataSampler? TryCreate(ICoreServerAPI api, out string reason)
    {
        GenRockStrataNew? generator = api.ModLoader.GetModSystem<GenRockStrataNew>();
        if (generator == null)
        {
            reason = "vanilla's rock strata generator is not loaded";
            return null;
        }

        IAsset? asset = api.Assets.TryGet(new AssetLocation("game", "worldgen/rockstrata.json"));
        RockStrataConfig? strata = asset?.ToObject<RockStrataConfig>();
        if (strata?.Variants == null || strata.Variants.Length == 0)
        {
            reason = "worldgen/rockstrata.json could not be read";
            return null;
        }

        foreach (RockStratum stratum in strata.Variants)
        {
            stratum.Init(api.World);
            if (stratum.Amplitudes.Length != stratum.Frequencies.Length || stratum.Amplitudes.Length != stratum.Thresholds.Length)
            {
                reason = $"worldgen/rockstrata.json's stratum {stratum.BlockCode} has octave lists of different lengths";
                return null;
            }
        }

        // The world's default rock from the global config, as vanilla's generators resolve it. GenRockStrataNew.rockBlockId
        // is only set when its own world generator starts, which can be after this, and read as 0 every column whose top is
        // the default rock came back with no rock at all.
        int defaultRockId = GlobalConfig.GetInstance(api).defaultRockId;
        if (defaultRockId <= 0)
        {
            reason = "worldgen/global.json names no default rock this world has";
            return null;
        }

        reason = "";
        return new RockStrataSampler(api, strata, defaultRockId);
    }

    /// <summary>
    /// The block code of the rock at the top of the column at (<paramref name="worldX"/>, <paramref name="worldZ"/>) whose
    /// terrain height is <paramref name="height"/>: what vanilla's strata put there, before the block layers go on top.
    /// Empty for a column before the world's first, which vanilla never generates.
    /// </summary>
    public string TopRock(int worldX, int worldZ, int height)
    {
        if (worldX < 0 || worldZ < 0)
            return "";

        int chunkX = worldX / Mapping.CHUNK_SIZE;
        int chunkZ = worldZ / Mapping.CHUNK_SIZE;
        return this.TopRockIn(this.Region(chunkX / this.regionChunkSize, chunkZ / this.regionChunkSize), worldX, worldZ, height);
    }

    /// <summary>A map region's own saved strata and provinces, made ready once for <see cref="TopRockFromSaved"/>.</summary>
    public sealed class SavedRegion
    {
        internal SavedRegion(RegionRock rock) => this.Rock = rock;

        internal RegionRock Rock { get; }
    }

    /// <summary>
    /// The world's own saved maps of <paramref name="mapRegion"/>, for <see cref="TopRockFromSaved"/>; null when the
    /// region has no saved strata or provinces.
    /// </summary>
    public static SavedRegion? Saved(IMapRegion mapRegion)
    {
        if (mapRegion?.RockStrata == null || mapRegion.RockStrata.Length == 0 || mapRegion.GeologicProvinceMap?.Data == null)
            return null;

        IntDataMap2D geo = mapRegion.GeologicProvinceMap;
        return new SavedRegion(new RegionRock(
            new LerpedWeightedIndex2DMap(geo.Data, geo.Size, TerraGenConfig.geoProvSmoothingRadius, geo.TopLeftPadding, geo.BottomRightPadding),
            mapRegion.RockStrata,
            geo.Data));
    }

    /// <summary>
    /// <see cref="TopRock"/> worked out from the world's own saved maps of a region rather than the sampler's: what
    /// vanilla's strata gave there with the maps it had. For checking the sampler (rockcheck).
    /// </summary>
    public string TopRockFromSaved(SavedRegion saved, int worldX, int worldZ, int height)
        => this.TopRockIn(saved.Rock, worldX, worldZ, height);

    /// <summary>
    /// How the world's saved maps of a map region compare with the sampler's own from the seed: how many strata maps
    /// differ, of how many, and whether the province map does.
    /// </summary>
    public (int StrataDiffer, int Strata, bool ProvincesDiffer) CompareSaved(IMapRegion mapRegion, int regionX, int regionZ)
    {
        RegionRock ours = this.Region(regionX, regionZ);
        int differ = 0;
        int count = Math.Min(ours.Strata.Length, mapRegion?.RockStrata?.Length ?? 0);
        for (int i = 0; i < count; i++)
        {
            int[]? saved = mapRegion!.RockStrata[i]?.Data;
            if (saved == null || !saved.AsSpan().SequenceEqual(ours.Strata[i].Data))
                differ++;
        }

        int[]? savedProvinces = mapRegion?.GeologicProvinceMap?.Data;
        bool provincesDiffer = savedProvinces == null || !savedProvinces.AsSpan().SequenceEqual(ours.ProvinceData);
        return (differ + Math.Max(0, ours.Strata.Length - count), ours.Strata.Length, provincesDiffer);
    }

    private string TopRockIn(RegionRock region, int worldX, int worldZ, int height)
    {
        int chunkX = worldX / Mapping.CHUNK_SIZE;
        int chunkZ = worldZ / Mapping.CHUNK_SIZE;
        int lx = worldX - (chunkX * Mapping.CHUNK_SIZE);
        int lz = worldZ - (chunkZ * Mapping.CHUNK_SIZE);
        int rdx = chunkX % this.regionChunkSize;
        int rdz = chunkZ % this.regionChunkSize;

        // GenRockStrataNew.preLoad and genBlockColumn: the province weights at the column, each rock group's most.
        float lerpMapInv = 1f / TerraGenConfig.geoProvMapScale;
        float[] weights = weightScratch is { } scratch && scratch.Length == this.provinces.Variants.Length
            ? scratch
            : weightScratch = new float[this.provinces.Variants.Length];
        _ = region.Provinces.WeightsAt((rdx * lerpMapInv * Mapping.CHUNK_SIZE) + (lx * lerpMapInv), (rdz * lerpMapInv * Mapping.CHUNK_SIZE) + (lz * lerpMapInv), weights);
        Span<float> groupMax = stackalloc float[4];
        for (int i = 0; i < weights.Length; i++)
        {
            float weight = weights[i];
            if (weight == 0f)
                continue;

            GeologicProvinceRockStrata[] indexed = this.provinces.Variants[i].RockStrataIndexed;
            for (int g = 0; g < 4; g++)
                groupMax[g] += indexed[g].ScaledMaxThickness * weight;
        }

        float distortX = (float)this.distortX.Noise(worldX, worldZ);
        float distortZ = (float)this.distortZ.Noise(worldX, worldZ);
        float distort = GameMath.Clamp((distortX + distortZ) / 30f, 0.9f, 1.1f);

        int strataCount = Math.Min(this.shapes.Length, region.Strata.Length);

        float Thickness(int index)
        {
            IntDataMap2D map = region.Strata[index];
            float scale = (float)map.InnerSize / this.regionChunkSize;
            float x = Math.Max((rdx * scale) + (scale * (lx + distortX) / Mapping.CHUNK_SIZE), -1.499f);
            float z = Math.Max((rdz * scale) + (scale * (lz + distortZ) / Mapping.CHUNK_SIZE), -1.499f);
            return map.GetIntLerpedCorrectly(x, z);
        }

        int top = RockColumn.TopStratum(height, this.seaLevel, this.shapes.AsSpan(0, strataCount), Thickness, groupMax, distort);
        return top < 0 ? this.defaultCode : this.codes[top];
    }

    private RegionRock Region(int regionX, int regionZ)
    {
        long key = ((long)regionX << 32) | (uint)regionZ;
        if (this.regions.TryGetValue(key, out RegionRock? known) && known != null)
            return known;

        lock (this.regionLocks[(int)((ulong)key % (ulong)this.regionLocks.Length)])
        {
            if (this.regions.TryGetValue(key, out known) && known != null)
                return known;

            RegionRock made = this.MakeRegion(regionX, regionZ);
            _ = this.regions.Set(key, made, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(30) });
            return made;
        }
    }

    // GenMaps.OnMapRegionGen's province map and GenRockStrataNew.OnMapRegionGen's strata maps, sized and padded as they are.
    private RegionRock MakeRegion(int regionX, int regionZ)
    {
        int provinceSize = this.regionSize / TerraGenConfig.geoProvMapScale;
        int provincePadding = TerraGenConfig.geoProvMapPadding;
        int[] provinceData;
        lock (this.provinceGen)
        {
            provinceData = this.provinceGen.GenLayer(
                (regionX * provinceSize) - provincePadding,
                (regionZ * provinceSize) - provincePadding,
                provinceSize + (2 * provincePadding),
                provinceSize + (2 * provincePadding));
        }

        LerpedWeightedIndex2DMap provinces = new(
            provinceData, provinceSize + (2 * provincePadding), TerraGenConfig.geoProvSmoothingRadius, provincePadding, provincePadding);

        int strataSize = this.regionSize / TerraGenConfig.rockStrataScale;
        const int strataPadding = 2;
        IntDataMap2D[] strataMaps = new IntDataMap2D[this.strataNoises.Length];
        for (int i = 0; i < strataMaps.Length; i++)
        {
            MapLayerCustomPerlin noise = this.strataNoises[i];
            IntDataMap2D map = new() { Size = strataSize + (2 * strataPadding), TopLeftPadding = strataPadding, BottomRightPadding = strataPadding };
            lock (noise)
            {
                map.Data = noise.GenLayer(
                    (regionX * strataSize) - strataPadding,
                    (regionZ * strataSize) - strataPadding,
                    strataSize + (2 * strataPadding),
                    strataSize + (2 * strataPadding));
            }

            strataMaps[i] = map;
        }

        return new RegionRock(provinces, strataMaps, provinceData);
    }

    private static string CodeOf(ICoreServerAPI api, int blockId)
        => blockId > 0 && blockId < api.World.Blocks.Count ? api.World.Blocks[blockId]?.Code?.ToString() ?? "" : "";

    internal sealed record RegionRock(LerpedWeightedIndex2DMap Provinces, IntDataMap2D[] Strata, int[] ProvinceData);
}
