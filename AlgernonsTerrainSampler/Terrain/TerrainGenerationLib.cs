using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;
using static AlgernonsTerrainSampler.Mapping;

namespace AlgernonsTerrainSampler.Terrain;

public static class TerrainGenerationLib
{
    private static ThreadLocal<Dictionary<(int chunkX, int chunkZ, bool includeClimate), TerrainGenerationContext>> contextCache = new(() => []);

    private const int MaxContextCacheSize = 256;

    public static void DisposeContextCache()
    {
        contextCache?.Dispose();
        contextCache = new(() => []);
    }

    public const double terrainDistortionMultiplier = 4.0;
    public const double terrainDistortionThreshold = 40.0;
    public const double geoDistortionMultiplier = 10.0;
    public const double geoDistortionThreshold = 10.0;
    public const double maxDistortionAmount = (55 + 40 + 30 + 10) * SimplexNoiseOctave.MAX_VALUE_2D_WARP;

    public struct VectorXZ
    {
        public double X, Z;
        public static VectorXZ operator *(VectorXZ a, double b) => new() { X = a.X * b, Z = a.Z * b };
    }

    public static double[] ScaleAdjustedFrequencies(double[] frequencies, float horizontalScale)
    {
        for (int i = 0; i < frequencies.Length; i++)
            frequencies[i] /= horizontalScale;

        return frequencies;
    }

    public static VectorXZ NewDistortionNoise(WorldMapCoordinate worldCoordinate, SimplexNoise distortion2dOnAxisX, SimplexNoise distortion2dOnAxisZ)
    {
        double noiseX = worldCoordinate.X / 400.0;
        double noiseZ = worldCoordinate.Z / 400.0;
        SimplexNoise.NoiseFairWarpVector(distortion2dOnAxisX, distortion2dOnAxisZ, noiseX, noiseZ, out double distortionForPosX, out double distortionForPosZ);

        return new VectorXZ { X = distortionForPosX, Z = distortionForPosZ };
    }

    public static float CalcOceanDistortion(
        RegionMapCorners oceanMapCorners,
        BlockInChunkMapCoordinate blockColumnInChunkCoordinate,
        int chunkSize,
        int mapSizeY)
    {
        float oceanicityFactor = mapSizeY / 256f * (1f / 3f);
        float chunkBlockDelta = 1.0f / chunkSize;
        return oceanicityFactor * oceanMapCorners.BiLerp(
            blockColumnInChunkCoordinate.X * chunkBlockDelta,
            blockColumnInChunkCoordinate.Z * chunkBlockDelta);
    }

    public static float CalcUpheavalDistortion(
        RegionMapCorners upheavalMapCorners,
        BlockInChunkMapCoordinate blockColumnInChunkCoordinate,
        WorldMapCoordinate worldCoordinate,
        VectorXZ geoDistortion,
        NormalizedSimplexNoise geoUpheavalNoise,
        int chunkSize)
    {
        float chunkBlockDelta = 1.0f / chunkSize;
        float upheavalStrength = upheavalMapCorners.BiLerp(
            blockColumnInChunkCoordinate.X * chunkBlockDelta,
            blockColumnInChunkCoordinate.Z * chunkBlockDelta);

        float upheavalNoiseValue = (float)geoUpheavalNoise.Noise((worldCoordinate.X + geoDistortion.X) / 400.0,
                                                                 (worldCoordinate.Z + geoDistortion.Z) / 400.0) * 0.9f;
        float upheavalMultiplier = Math.Min(0, 0.5f - upheavalNoiseValue);

        return upheavalStrength * upheavalMultiplier;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FindNoiseSignAtHeight(
        TerrainSamplerNormalizedSimplexFractalNoise.ColumnNoise columnNoise,
        int posY,
        double threshold)
    {
        if (threshold >= columnNoise.BoundMax)
            return double.NegativeInfinity;

        if (threshold <= columnNoise.BoundMin)
            return double.PositiveInfinity;

        return columnNoise.NoiseSign(posY, -NormalizedSimplexNoise.NoiseValueCurveInverse(threshold));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void StartSampleDisplacedYThreshold(float distortedPosY, int mapSizeYminus2, out int yBase, out float ySlide)
    {
        int distortedPosYBase = (int)Math.Floor(distortedPosY);
        yBase = GameMath.Clamp(distortedPosYBase, 0, mapSizeYminus2);
        ySlide = distortedPosY - distortedPosYBase;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ContinueSampleDisplacedYThreshold(int yBase, float ySlide, float[] thresholds)
        => GameMath.Lerp(thresholds[yBase], thresholds[yBase + 1], ySlide);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SumLandformThresholdAt(
        int distortedPosYBase,
        float distortedPosYSlide,
        WeightedIndex[] columnWeightedIndices,
        LandformsWorldProperty worldLandforms)
    {
        double threshold = 0;
        for (int i = 0; i < columnWeightedIndices.Length; i++)
        {
            float weight = columnWeightedIndices[i].Weight;
            if (weight != 0f)
            {
                float[] thresholds = worldLandforms.LandFormsByIndex[columnWeightedIndices[i].Index].TerrainYThresholds;
                threshold += ContinueSampleDisplacedYThreshold(distortedPosYBase, distortedPosYSlide, thresholds) * weight;
            }
        }

        return threshold;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double CalcGeoUpheavalTaper(
        double posY, double distY, double taperThreshold, double geoUpheavalAmplitude, double mapSizeY)
    {
        const double AMPLITUDE_MODIFIER = 40.0;
        if (posY > taperThreshold && distY < -2)
        {
            double upheavalAmount = GameMath.Clamp(-distY, posY - mapSizeY, posY);
            double ceilingDelta = posY - taperThreshold;

            return ceilingDelta * upheavalAmount / (AMPLITUDE_MODIFIER * geoUpheavalAmplitude);
        }

        return 0;
    }

    public static VectorXZ ApplyIsotropicDistortionThreshold(VectorXZ distortion, double threshold, double maximum)
    {
        double distortionMagnitudeSquared = (distortion.X * distortion.X) + (distortion.Z * distortion.Z);
        double thresholdSquared = threshold * threshold;
        if (distortionMagnitudeSquared <= thresholdSquared)
        {
            distortion.X = distortion.Z = 0;
        }
        else
        {
            double baseCurve = (distortionMagnitudeSquared - thresholdSquared) / distortionMagnitudeSquared;
            double maximumSquared = maximum * maximum;
            double baseCurveReciprocalAtMaximum = maximumSquared / (maximumSquared - thresholdSquared);
            double slide = baseCurve * baseCurveReciprocalAtMaximum;

            slide *= slide;

            double expectedOutputMaximum = maximum - threshold;
            double forceDown = slide * (expectedOutputMaximum / maximum);

            distortion *= forceDown;
        }

        return distortion;
    }

    public static LerpedWeightedIndex2DMap GetOrLoadLerpedLandformMap(
        IntDataMap2D landformMap,
        int regionX,
        int regionZ,
        ConcurrentDictionary<int, LerpedWeightedIndex2DMap> LandformMapByRegion,
        int regionMapSize)
    {
        int key = (regionZ * regionMapSize) + regionX;

        return LandformMapByRegion.GetOrAdd(key, _ => new LerpedWeightedIndex2DMap(
            landformMap.Data,
            landformMap.Size,
            TerraGenConfig.landFormSmoothingRadius,
            landformMap.TopLeftPadding,
            landformMap.BottomRightPadding));
    }

    public static int CalculateColumnHeight(
        BlockInChunkMapCoordinate blockColumnInChunkCoordinate,
        TerrainGenerationContext context,
        LandformsWorldProperty worldLandforms,
        TerrainSamplerNormalizedSimplexFractalNoise.ColumnNoise columnNoise,
        float heightDistortion,
        int mapSizeY)
    {
        int columnHeight = 0;
        int taperThreshold = (int)(mapSizeY * 0.9f);
        double geoUpheavalAmplitude = 255;

        float chunkPixelBlockStep = context.ChunkPixelSize * context.ChunkBlockDelta;

        WeightedIndex[] columnWeightedIndices = context.LandLerpMap[
            context.RegionMapChunkStartCoordinate.X + (blockColumnInChunkCoordinate.X * chunkPixelBlockStep),
            context.RegionMapChunkStartCoordinate.Z + (blockColumnInChunkCoordinate.Z * chunkPixelBlockStep)];

        for (int posY = 1; posY < mapSizeY - 1; posY++)
        {
            StartSampleDisplacedYThreshold(
                posY + heightDistortion,
                mapSizeY - 2,
                out int distortedPosYBase,
                out float distortedPosYSlide);

            double threshold = SumLandformThresholdAt(distortedPosYBase, distortedPosYSlide, columnWeightedIndices, worldLandforms);

            double geoUpheavalTaper = CalcGeoUpheavalTaper(posY, heightDistortion, taperThreshold, geoUpheavalAmplitude, mapSizeY);
            threshold += geoUpheavalTaper;

            double noiseSign = FindNoiseSignAtHeight(columnNoise, posY, threshold);

            if (noiseSign > 0)
                columnHeight = posY;
        }

        return columnHeight;
    }

    public static float CalculateRainfallFromClimate(
        BlockInChunkMapCoordinate blockColumnInChunkCoordinate,
        TerrainGenerationContext context,
        int chunkSize,
        int terrainHeight,
        WorldMapCoordinate worldCoordinate,
        SimplexNoise distort2dOnAxisX,
        SimplexNoise distort2dOnAxisZ)
    {
        if (context.ClimateMapCorners.IsEmpty)
            return -1f;

        double distortionForPosX = distort2dOnAxisX.Noise(worldCoordinate.X, worldCoordinate.Z);
        double distortionForPosZ = distort2dOnAxisZ.Noise(worldCoordinate.X, worldCoordinate.Z);

        float chunkBlockDelta = 1.0f / chunkSize;
        float distortedX = (blockColumnInChunkCoordinate.X * chunkBlockDelta) + ((float)distortionForPosX / chunkSize);
        float distortedZ = (blockColumnInChunkCoordinate.Z * chunkBlockDelta) + ((float)distortionForPosZ / chunkSize);

        int climate = context.ClimateMapCorners.BiLerpRgbColor(distortedX, distortedZ);

        int rainfallRaw = (climate >> 8) & 255;

        int distortionForY = (int)(distortionForPosX / 5.0);
        int adjustedHeight = terrainHeight + distortionForY;

        int seaLevel = TerraGenConfig.seaLevel;
        int heightAdjustedRainfall = Math.Clamp(
            rainfallRaw + ((adjustedHeight - seaLevel) / 2) + (5 * Math.Clamp(8 + seaLevel - adjustedHeight, 0, 8)),
            0,
            255);

        return heightAdjustedRainfall / 255f;
    }

    public static int SampleClimateColor(
        BlockInChunkMapCoordinate blockColumnInChunkCoordinate,
        TerrainGenerationContext context,
        int chunkSize,
        WorldMapCoordinate worldCoordinate,
        SimplexNoise distort2dOnAxisX,
        SimplexNoise distort2dOnAxisZ)
    {
        if (context.ClimateMapCorners.IsEmpty)
            return 0;

        double distortionForPosX = distort2dOnAxisX.Noise(worldCoordinate.X, worldCoordinate.Z);
        double distortionForPosZ = distort2dOnAxisZ.Noise(worldCoordinate.X, worldCoordinate.Z);

        float chunkBlockDelta = 1.0f / chunkSize;
        float distortedX = (blockColumnInChunkCoordinate.X * chunkBlockDelta) + ((float)distortionForPosX / chunkSize);
        float distortedZ = (blockColumnInChunkCoordinate.Z * chunkBlockDelta) + ((float)distortionForPosZ / chunkSize);

        return context.ClimateMapCorners.BiLerpRgbColor(distortedX, distortedZ);
    }

    public static int ApplySeaLevelRiseAdjustment(int baseHeight, float rainfall, int seaLevel)
    {
        if (baseHeight < seaLevel)
        {
            int sealevelrise = (int)Math.Min(Math.Max(0f, (0.5f - rainfall) * 40f), seaLevel - baseHeight);
            return Math.Max(baseHeight + sealevelrise - 1, baseHeight);
        }

        return baseHeight;
    }

    public struct TerrainGenerationContext
    {
        public MapCoordinateFloat RegionMapChunkStartCoordinate;
        public float ChunkPixelSize;
        public float ChunkBlockDelta;
        public RegionMapCorners UpheavalMapCorners;
        public RegionMapCorners OceanMapCorners;
        public RegionMapCorners ClimateMapCorners;
        public LerpedWeightedIndex2DMap LandLerpMap;
        public TerrainOctaves TerrainOctaves;
    }

    // Padding numbers are hardcoded in, so they can get mismatched from basegame
    // One time one of them was incorrect in the basegame, then they fixed it which misaligned this
    // One day they might be moved to TerraGenConfig where they belong
    public static TerrainGenerationContext CreateTerrainGenerationContext(
        TerrainSamplerGenTerra genTerra,
        SmallChunkMapCoordinate currentChunkCoordinate,
        bool includeClimate = false)
    {
        Dictionary<(int chunkX, int chunkZ, bool includeClimate), TerrainGenerationContext> cache = contextCache.Value;
        (int X, int Z, bool includeClimate) cacheKey = (currentChunkCoordinate.X, currentChunkCoordinate.Z, includeClimate);

        if (cache.TryGetValue(cacheKey, out TerrainGenerationContext cachedContext))
            return cachedContext;

        int regionChunkSize = genTerra.RegionChunkSize;
        int regionSize = genTerra.RegionSize;
        GenMaps genMaps = genTerra.BasegameGenMaps;

        ChunkInRegionMapCoordinate chunkInRegionCoordinate = currentChunkCoordinate.ToChunkInRegion(regionChunkSize);
        RegionMapCoordinate regionCoordinate = currentChunkCoordinate.ToRegion(regionChunkSize);

        RegionMapCorners LoadCorners(MapLayerBase gen, int padding, int scale, string name)
        {
            IntDataMap2D regionMap = ThreadSafeRegionCache.GetRegionMap(regionCoordinate, regionSize, padding, scale, gen, name);
            return new RegionMapCorners(regionMap, regionChunkSize, chunkInRegionCoordinate);
        }

        IntDataMap2D regionLandformsMap = ThreadSafeRegionCache.GetRegionMap(
            regionCoordinate, regionSize,
            TerraGenConfig.landformMapPadding, TerraGenConfig.landformMapScale,
            genMaps.landformsGen, "landform");

        RegionMapCorners upheavalMapCorners = LoadCorners(genMaps.upheavelGen, 3, TerraGenConfig.climateMapScale, "upheaval");
        RegionMapCorners oceanMapCorners = LoadCorners(genMaps.oceanGen, 5, TerraGenConfig.oceanMapScale, "ocean");
        RegionMapCorners climateMapCorners;
        if (includeClimate)
            climateMapCorners = LoadCorners(genMaps.climateGen, 2, TerraGenConfig.climateMapScale, "climate");
        else
            climateMapCorners = default;

        LerpedWeightedIndex2DMap landLerpMap = GetOrLoadLerpedLandformMap(
            regionLandformsMap,
            regionCoordinate.X,
            regionCoordinate.Z,
            genTerra.LandformMapByRegion,
            genTerra.RegionMapSize);

        float chunkPixelSize = regionLandformsMap.InnerSize / regionChunkSize;
        float chunkBlockDelta = 1.0f / CHUNK_SIZE;

        MapCoordinateFloat regionMapChunkStartCoordinate = chunkInRegionCoordinate.ScaleTo(chunkPixelSize);

        TerrainOctaves terrainOctaves = new(landLerpMap, chunkPixelSize, regionMapChunkStartCoordinate, genTerra.NumberOfOctaves, genTerra.Landforms);

        TerrainGenerationContext result = new()
        {
            RegionMapChunkStartCoordinate = regionMapChunkStartCoordinate,
            ChunkPixelSize = chunkPixelSize,
            ChunkBlockDelta = chunkBlockDelta,
            UpheavalMapCorners = upheavalMapCorners,
            OceanMapCorners = oceanMapCorners,
            ClimateMapCorners = climateMapCorners,
            LandLerpMap = landLerpMap,
            TerrainOctaves = terrainOctaves
        };

        if (cache.Count >= MaxContextCacheSize)
            cache.Clear();
        cache[cacheKey] = result;

        return result;
    }
}
