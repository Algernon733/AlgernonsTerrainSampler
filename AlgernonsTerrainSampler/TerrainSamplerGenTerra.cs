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

    public ConcurrentDictionary<int, LerpedWeightedIndex2DMap> LandformMapByRegion = new();
    private readonly ConcurrentDictionary<int, IntDataMap2D> forestMapByRegion = new();
    private readonly ConcurrentDictionary<int, IntDataMap2D> shrubMapByRegion = new();

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

    public GenMaps BasegameGenMaps { get; set; }

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
        this.LandformMapByRegion.Clear();
        this.forestMapByRegion.Clear();
        this.shrubMapByRegion.Clear();

        if (GenMapsSubstitutions.NoiseLandforms.LandformsProperty == null)
            GenMapsSubstitutions.NoiseLandforms.LoadLandforms(this.api);

        this.Landforms = GenMapsSubstitutions.NoiseLandforms.LandformsProperty;

        this.RegionMapSize = (int)Math.Ceiling((double)this.api.WorldManager.MapSizeX / this.api.WorldManager.RegionSize);
        this.RegionChunkSize = this.RegionSize / CHUNK_SIZE;

        this.NoiseScale = Math.Max(1, this.api.WorldManager.MapSizeY / 256f);
        this.NumberOfOctaves = TerraGenConfig.GetTerrainOctaveCount(this.api.WorldManager.MapSizeY);

        this.TerrainNoise = TerrainSamplerNormalizedSimplexFractalNoise.FromDefaultOctaves(
            this.NumberOfOctaves, 0.0005 * NewSimplexNoiseLayer.OldToNewFrequency / this.NoiseScale, 0.9, this.api.WorldManager.Seed);

        SimplexNoise DistortionNoise(long seedOffset) => new(
            amplitudes:  [55, 40, 30, 10],
            frequencies: ScaleAdjustedFrequencies([1/5.0, 1/2.50, 1/1.250, 1/0.65], this.NoiseScale),
            seed: this.api.World.Seed + 9876 + seedOffset);

        this.Distort2dOnAxisX = DistortionNoise(0);
        this.Distort2dOnAxisZ = DistortionNoise(2);

        this.GeoUpheavalNoise = new NormalizedSimplexNoise(
            [55, 40, 30, 15, 7, 4],
            ScaleAdjustedFrequencies([1.0/5.5, 1.1/2.75, 1.2/1.375, 1.2/0.715, 1.2/0.45, 1.2/0.25], this.NoiseScale),
            this.api.World.Seed + 9876 + 1);

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
        GenMaps genMaps = this.BasegameGenMaps;
        if (genMaps?.landformsGen == null || genMaps.upheavelGen == null || genMaps.oceanGen == null)
            throw new InvalidOperationException("Terrain generators not initialized.");

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
        GenMaps genMaps = this.BasegameGenMaps;
        if (genMaps?.landformsGen == null || genMaps.upheavelGen == null || genMaps.oceanGen == null || genMaps.climateGen == null)
            throw new InvalidOperationException("Terrain generators not initialized.");

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

        GenMaps genMaps = this.BasegameGenMaps;
        if (genMaps?.climateGen == null || genMaps.forestGen == null || genMaps.bushGen == null)
            return;

        ChunkInRegionMapCoordinate chunkInRegionCoordinate = chunkCoordinate.ToChunkInRegion(this.RegionChunkSize);
        RegionMapCoordinate regionCoordinate = chunkCoordinate.ToRegion(this.RegionChunkSize);

        IntDataMap2D climateMap = ThreadSafeRegionCache.GetRegionMap(
            regionCoordinate, this.RegionSize, 2, TerraGenConfig.climateMapScale, genMaps.climateGen, "climate");

        IntDataMap2D forestMap = this.GetOrCreateClimateDerivedMap(
            this.forestMapByRegion, regionCoordinate, climateMap, genMaps.forestGen, TerraGenConfig.forestMapScale);

        IntDataMap2D shrubMap = this.GetOrCreateClimateDerivedMap(
            this.shrubMapByRegion, regionCoordinate, climateMap, genMaps.bushGen, TerraGenConfig.shrubMapScale);

        forestDensity = SampleDensityMap(forestMap, chunkInRegionCoordinate, blockColumnInChunkCoordinate);
        shrubDensity = SampleDensityMap(shrubMap, chunkInRegionCoordinate, blockColumnInChunkCoordinate);
    }

    private IntDataMap2D GetOrCreateClimateDerivedMap(
        ConcurrentDictionary<int, IntDataMap2D> cache,
        RegionMapCoordinate regionCoordinate,
        IntDataMap2D climateMap,
        MapLayerBase generator,
        int scale)
    {
        int key = (regionCoordinate.Z * this.RegionMapSize) + regionCoordinate.X;
        return cache.GetOrAdd(key, _ =>
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
