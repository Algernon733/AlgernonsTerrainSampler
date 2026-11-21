using System;
using System.Collections.Concurrent;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;
using AlgernonsTerrainSampler.Terrain;
using static AlgernonsTerrainSampler.Mapping;
using static AlgernonsTerrainSampler.Terrain.TerrainGenerationLib;

namespace AlgernonsTerrainSampler;

/// <summary>
/// Trimmed-down GenTerra that only keeps the fields and methods needed for sampling terrain height
/// </summary>
public class TerrainSamplerGenTerra : ModStdWorldGen
{
    public struct ThreadLocalTempData
    {
        public double[] LerpedAmplitudes;
        public double[] LerpedThresholds;
    }

    private ICoreServerAPI api;

    // The regions kept of each. A landform map's weights are about 80 KiB a region; forest and shrub maps are small.
    private const int LandformRegionsKept = 512;
    private const int DensityRegionsKept = 1024;

    public RegionCache<LerpedWeightedIndex2DMap> LandformMapByRegion { get; } = new(LandformRegionsKept);
    private readonly RegionCache<IntDataMap2D> forestMapByRegion = new(DensityRegionsKept);
    private readonly RegionCache<IntDataMap2D> shrubMapByRegion = new(DensityRegionsKept);

    public int RegionMapSize { get; private set; }
    public int MapSizeY => this.api.WorldManager.MapSizeY;
    public int RegionSize => this.api.WorldManager.RegionSize;
    public int RegionChunkSize { get; private set; }
    public float NoiseScale { get; private set; }
    public int NumberOfOctaves { get; private set; }

    public TerrainSamplerNormalizedSimplexFractalNoise TerrainNoise { get; private set; }
    public SimplexNoise Distort2dOnAxisX { get; private set; }
    public SimplexNoise Distort2dOnAxisZ { get; private set; }
    public NormalizedSimplexNoise GeoUpheavalNoise { get; private set; }
    public LandformsWorldProperty Landforms;

    public ThreadLocal<ThreadLocalTempData> TempDataThreadLocal { get; private set; }

    /// <summary>
    /// The game's own map generators. No longer read by the sampler, which keeps its own (<see cref="Maps"/>): the game
    /// uses these from its worldgen thread without a lock.
    /// </summary>
    public GenMaps BasegameGenMaps { get; set; }

    /// <summary>The sampler's own map generators for this world.</summary>
    public GenMapsSubstitutions.SamplerGenMaps Maps => this.maps ??= this.api?.ModLoader.GetModSystem<GenMapsSubstitutions.SamplerGenMaps>();

    private GenMapsSubstitutions.SamplerGenMaps maps;

    /// <summary>
    /// Whether this world's generators have started, so that sampling can give real answers.
    /// False on any world type but "standard", whose generator this reimplements.
    /// </summary>
    public bool IsReady => this.TerrainNoise != null && this.Maps?.IsReady == true;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override double ExecuteOrder() => 0;
    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        api.Event.InitWorldGenerator(this.InitWorldGen, "standard");
    }

    public void InitWorldGen()
    {
        this.LoadGlobalConfig(this.api);
        // Cleared, not replaced: a world generator started again while the game runs (/wgen regen) must not dispose what
        // samples in flight are reading.
        this.LandformMapByRegion.Clear();
        this.forestMapByRegion.Clear();
        this.shrubMapByRegion.Clear();

        // Every world loads its own: landform thresholds depend on the world height, and mods can patch the asset.
        GenMapsSubstitutions.NoiseLandforms.LoadLandforms(this.api, reload: true);

        this.Landforms = GenMapsSubstitutions.NoiseLandforms.LandformsProperty;

        this.RegionMapSize = (int)Math.Ceiling((double)this.api.WorldManager.MapSizeX / this.api.WorldManager.RegionSize);
        this.RegionChunkSize = this.RegionSize / CHUNK_SIZE;

        this.NoiseScale = Math.Max(1, this.api.WorldManager.MapSizeY / 256f);
        this.NumberOfOctaves = TerraGenConfig.GetTerrainOctaveCount(this.api.WorldManager.MapSizeY);

        int seed = WorldgenSeed.Of(this.api.WorldManager);

        this.TerrainNoise = TerrainSamplerNormalizedSimplexFractalNoise.FromDefaultOctaves(
            this.NumberOfOctaves, 0.0005 * NewSimplexNoiseLayer.OldToNewFrequency / this.NoiseScale, 0.9, seed);

        SimplexNoise DistortionNoise(long seedOffset) => new(
            amplitudes:  [55, 40, 30, 10],
            frequencies: ScaleAdjustedFrequencies([1/5.0, 1/2.50, 1/1.250, 1/0.65], this.NoiseScale),
            seed: seed + 9876 + seedOffset);

        this.Distort2dOnAxisX = DistortionNoise(0);
        this.Distort2dOnAxisZ = DistortionNoise(2);

        this.GeoUpheavalNoise = new NormalizedSimplexNoise(
            [55, 40, 30, 15, 7, 4],
            ScaleAdjustedFrequencies([1.0/5.5, 1.1/2.75, 1.2/1.375, 1.2/0.715, 1.2/0.45, 1.2/0.25], this.NoiseScale),
            seed + 9876 + 1);

        this.TempDataThreadLocal = new ThreadLocal<ThreadLocalTempData>(() => new ThreadLocalTempData
        {
            LerpedAmplitudes = new double[this.NumberOfOctaves],
            LerpedThresholds = new double[this.NumberOfOctaves]
        });
    }

    public override void Dispose()
    {
        this.TempDataThreadLocal?.Dispose();
        this.TempDataThreadLocal = null;
        this.LandformMapByRegion.Dispose();
        this.forestMapByRegion.Dispose();
        this.shrubMapByRegion.Dispose();
        base.Dispose();
    }

    public (VectorXZ terrainDistortion, float heightDistortion, float oceanDistortion) CreateDistortionEffectsForColumn(
        WorldMapCoordinate worldCoordinate,
        BlockInChunkMapCoordinate blockColumnInChunkCoordinate,
        RegionMapCorners upheavalMapCorners,
        RegionMapCorners oceanMapCorners)
    {
        VectorXZ baseDistortion = NewDistortionNoise(worldCoordinate, this.Distort2dOnAxisX, this.Distort2dOnAxisZ);

        VectorXZ terrainDistortion = ApplyIsotropicDistortionThreshold(
                baseDistortion * terrainDistortionMultiplier,
                terrainDistortionThreshold,
                terrainDistortionMultiplier * maxDistortionAmount);

        VectorXZ geoDistortion = ApplyIsotropicDistortionThreshold(
            baseDistortion * geoDistortionMultiplier,
            geoDistortionThreshold,
            geoDistortionMultiplier * maxDistortionAmount);

        float upheavalDistortion = CalcUpheavalDistortion(
            upheavalMapCorners,
            blockColumnInChunkCoordinate,
            worldCoordinate,
            geoDistortion,
            this.GeoUpheavalNoise,
            CHUNK_SIZE);

        float oceanDistortion = CalcOceanDistortion(
            oceanMapCorners,
            blockColumnInChunkCoordinate,
            CHUNK_SIZE,
            this.MapSizeY);

        float heightDistortion = oceanDistortion + upheavalDistortion;

        return (terrainDistortion, heightDistortion, oceanDistortion);
    }

    public TerrainSamplerNormalizedSimplexFractalNoise.ColumnNoise CreateColumnNoise(
        BlockInChunkMapCoordinate blockColumnInChunkCoordinate,
        TerrainOctaves terrainOctaves,
        WorldMapCoordinate columnWorldCoordinate,
        VectorXZ terrainDistortion)
    {
        float chunkBlockDelta = 1.0f / CHUNK_SIZE;
        float blockX = blockColumnInChunkCoordinate.X * chunkBlockDelta;
        float blockZ = blockColumnInChunkCoordinate.Z * chunkBlockDelta;

        ThreadLocalTempData tempData = this.TempDataThreadLocal.Value;
        double[] lerpedAmplitudes = tempData.LerpedAmplitudes;
        double[] lerpedThresholds = tempData.LerpedThresholds;

        for (int i = 0; i < this.NumberOfOctaves; i++)
        {
            lerpedAmplitudes[i] = GameMath.BiLerp(
                terrainOctaves.OctaveNoiseX0[i],
                terrainOctaves.OctaveNoiseX1[i],
                terrainOctaves.OctaveNoiseX2[i],
                terrainOctaves.OctaveNoiseX3[i],
                blockX,
                blockZ);
            lerpedThresholds[i] = GameMath.BiLerp(
                terrainOctaves.OctaveThresholdX0[i],
                terrainOctaves.OctaveThresholdX1[i],
                terrainOctaves.OctaveThresholdX2[i],
                terrainOctaves.OctaveThresholdX3[i],
                blockX,
                blockZ);
        }

        double verticalNoiseRelativeFrequency = 0.5 / TerraGenConfig.terrainNoiseVerticalScale;

        return this.TerrainNoise.ForColumn(
            verticalNoiseRelativeFrequency,
            lerpedAmplitudes,
            lerpedThresholds,
            columnWorldCoordinate.X + terrainDistortion.X,
            columnWorldCoordinate.Z + terrainDistortion.Z);
    }

    public int GetBlockColumnHeight(WorldMapCoordinate worldCoordinate)
    {
        if (this.Maps?.IsReady != true)
            throw new InvalidOperationException("Terrain generators not initialized.");
        if (this.TerrainNoise == null)
            throw new InvalidOperationException("Terrain noise not initialized; only the standard world type can be sampled.");

        SmallChunkMapCoordinate currentChunkCoordinate = worldCoordinate.ToChunk();
        TerrainGenerationContext context = CreateTerrainGenerationContext(this, currentChunkCoordinate, includeClimate: true);

        BlockInChunkMapCoordinate blockColumnInChunkCoordinate = worldCoordinate.ToBlockInChunk();

        (VectorXZ terrainDistortion, float heightDistortion, _) = this.CreateDistortionEffectsForColumn(
            worldCoordinate, blockColumnInChunkCoordinate, context.UpheavalMapCorners, context.OceanMapCorners);

        TerrainSamplerNormalizedSimplexFractalNoise.ColumnNoise columnNoise = this.CreateColumnNoise(
            blockColumnInChunkCoordinate, context.TerrainOctaves, worldCoordinate, terrainDistortion);

        int baseHeight = CalculateColumnHeight(
            blockColumnInChunkCoordinate, context, this.Landforms, columnNoise, heightDistortion, this.MapSizeY);

        float rainfall = CalculateRainfallFromClimate(
            blockColumnInChunkCoordinate, context, CHUNK_SIZE, baseHeight, worldCoordinate, this.Distort2dOnAxisX, this.Distort2dOnAxisZ);
        if (rainfall >= 0f)
            baseHeight = ApplySeaLevelRiseAdjustment(baseHeight, rainfall, TerraGenConfig.seaLevel);

        return baseHeight;
    }

    public TerrainColumnSample SampleColumn(WorldMapCoordinate worldCoordinate)
        => this.SampleColumn(worldCoordinate, null);

    public TerrainColumnSample SampleColumn(WorldMapCoordinate worldCoordinate, int? heightOverride)
    {
        if (this.Maps?.IsReady != true)
            throw new InvalidOperationException("Terrain generators not initialized.");
        if (this.TerrainNoise == null)
            throw new InvalidOperationException("Terrain noise not initialized; only the standard world type can be sampled.");

        SmallChunkMapCoordinate currentChunkCoordinate = worldCoordinate.ToChunk();
        TerrainGenerationContext context = CreateTerrainGenerationContext(this, currentChunkCoordinate, includeClimate: true);
        BlockInChunkMapCoordinate blockColumnInChunkCoordinate = worldCoordinate.ToBlockInChunk();

        int height;
        if (heightOverride.HasValue)
        {
            height = heightOverride.Value;
        }
        else
        {
            (VectorXZ terrainDistortion, float heightDistortion, _) = this.CreateDistortionEffectsForColumn(
                worldCoordinate, blockColumnInChunkCoordinate, context.UpheavalMapCorners, context.OceanMapCorners);

            TerrainSamplerNormalizedSimplexFractalNoise.ColumnNoise columnNoise = this.CreateColumnNoise(
                blockColumnInChunkCoordinate, context.TerrainOctaves, worldCoordinate, terrainDistortion);

            height = CalculateColumnHeight(
                blockColumnInChunkCoordinate, context, this.Landforms, columnNoise, heightDistortion, this.MapSizeY);
        }

        float rainfall = CalculateRainfallFromClimate(
            blockColumnInChunkCoordinate, context, CHUNK_SIZE, height, worldCoordinate, this.Distort2dOnAxisX, this.Distort2dOnAxisZ);

        if (!heightOverride.HasValue && rainfall >= 0f)
            height = ApplySeaLevelRiseAdjustment(height, rainfall, TerraGenConfig.seaLevel);

        int climateColor = SampleClimateColor(
            blockColumnInChunkCoordinate, context, CHUNK_SIZE, worldCoordinate, this.Distort2dOnAxisX, this.Distort2dOnAxisZ);

        this.SampleForestAndShrubDensity(currentChunkCoordinate, blockColumnInChunkCoordinate, out float forestDensity, out float shrubDensity);

        return new TerrainColumnSample
        {
            Height = height,
            ClimateColor = climateColor,
            Rainfall = Math.Clamp(rainfall, 0f, 1f),
            Temperature = ((climateColor >> 16) & 0xFF) / 255f,
            ForestDensity = forestDensity,
            ShrubDensity = shrubDensity
        };
    }

    private void SampleForestAndShrubDensity(
        SmallChunkMapCoordinate chunkCoordinate,
        BlockInChunkMapCoordinate blockColumnInChunkCoordinate,
        out float forestDensity,
        out float shrubDensity)
    {
        forestDensity = 0f;
        shrubDensity = 0f;

        GenMapsSubstitutions.SamplerGenMaps maps = this.Maps;
        if (maps?.IsReady != true)
            return;

        ChunkInRegionMapCoordinate chunkInRegionCoordinate = chunkCoordinate.ToChunkInRegion(this.RegionChunkSize);
        RegionMapCoordinate regionCoordinate = chunkCoordinate.ToRegion(this.RegionChunkSize);
        RegionMaps regionMaps = this.MapsOfRegion(regionCoordinate, maps);

        forestDensity = SampleDensityMap(regionMaps.Forest, chunkInRegionCoordinate, blockColumnInChunkCoordinate);
        shrubDensity = SampleDensityMap(regionMaps.Shrub, chunkInRegionCoordinate, blockColumnInChunkCoordinate);
    }

    /// <summary>A map region's maps as the sampler makes them, padded as vanilla's <c>GenMaps.OnMapRegionGen</c> pads its own.</summary>
    internal readonly record struct RegionMaps(IntDataMap2D Climate, IntDataMap2D Upheaval, IntDataMap2D Ocean, IntDataMap2D Landform, IntDataMap2D Forest, IntDataMap2D Shrub);

    /// <summary>
    /// The maps the sampler reads for <paramref name="regionCoordinate"/>, from its caches: for comparing with the maps the
    /// world saved (<c>/terrainsampler mapcheck</c>).
    /// </summary>
    internal RegionMaps MapsOfRegion(RegionMapCoordinate regionCoordinate, GenMapsSubstitutions.SamplerGenMaps maps)
    {
        IntDataMap2D climate = ThreadSafeRegionCache.GetRegionMap(
            regionCoordinate, this.RegionSize, 2, TerraGenConfig.climateMapScale, maps.ClimateGen, "climate", maps.ClimateLock);

        return new RegionMaps(
            climate,
            ThreadSafeRegionCache.GetRegionMap(regionCoordinate, this.RegionSize, 3, TerraGenConfig.climateMapScale, maps.UpheavalGen, "upheaval"),
            ThreadSafeRegionCache.GetRegionMap(regionCoordinate, this.RegionSize, 5, TerraGenConfig.oceanMapScale, maps.OceanGen, "ocean"),
            ThreadSafeRegionCache.GetRegionMap(
                regionCoordinate, this.RegionSize, TerraGenConfig.landformMapPadding, TerraGenConfig.landformMapScale, maps.LandformsGen, "landform", maps.LandformsLock),
            this.GetOrCreateClimateDerivedMap(this.forestMapByRegion, regionCoordinate, climate, maps.ForestGen, TerraGenConfig.forestMapScale),
            this.GetOrCreateClimateDerivedMap(this.shrubMapByRegion, regionCoordinate, climate, maps.ShrubGen, TerraGenConfig.shrubMapScale));
    }

    private IntDataMap2D GetOrCreateClimateDerivedMap(
        RegionCache<IntDataMap2D> cache,
        RegionMapCoordinate regionCoordinate,
        IntDataMap2D climateMap,
        MapLayerBase generator,
        int scale)
    {
        return cache.GetOrAdd(regionCoordinate.X, regionCoordinate.Z, () =>
        {
            IntDataMap2D outputMap = new();
            int size = this.RegionSize / scale;
            outputMap.Size = size + 1;
            outputMap.BottomRightPadding = 1;

            lock (generator)
            {
                generator.SetInputMap(climateMap, outputMap);
                outputMap.Data = generator.GenLayer(regionCoordinate.X * size, regionCoordinate.Z * size, size + 1, size + 1);
            }

            return outputMap;
        });
    }

    private float SampleDensityMap(
        IntDataMap2D map,
        ChunkInRegionMapCoordinate chunkInRegionCoordinate,
        BlockInChunkMapCoordinate blockColumnInChunkCoordinate)
    {
        float factor = (float)map.InnerSize / this.RegionChunkSize;
        int upLeft = map.GetUnpaddedInt((int)(chunkInRegionCoordinate.X * factor), (int)(chunkInRegionCoordinate.Z * factor));
        int upRight = map.GetUnpaddedInt((int)((chunkInRegionCoordinate.X * factor) + factor), (int)(chunkInRegionCoordinate.Z * factor));
        int botLeft = map.GetUnpaddedInt((int)(chunkInRegionCoordinate.X * factor), (int)((chunkInRegionCoordinate.Z * factor) + factor));
        int botRight = map.GetUnpaddedInt((int)((chunkInRegionCoordinate.X * factor) + factor), (int)((chunkInRegionCoordinate.Z * factor) + factor));

        return GameMath.BiLerp(
            upLeft,
            upRight,
            botLeft,
            botRight,
            blockColumnInChunkCoordinate.X / (float)CHUNK_SIZE,
            blockColumnInChunkCoordinate.Z / (float)CHUNK_SIZE) / 255f;
    }
}
