using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.ServerMods;
using AlgernonsTerrainSampler.Terrain;
using static AlgernonsTerrainSampler.Terrain.TerrainGenerationLib;

namespace AlgernonsTerrainSampler;

public class TerrainSamplerCommand(TerrainSamplerMod mod)
{
    public TextCommandResult CmdColumnHeight(TextCommandCallingArgs args)
    {
        EntityPos playerPosition = args.Caller.Player.Entity.Pos;

        int x = (int)playerPosition.X;
        int z = (int)playerPosition.Z;

        WorldMapCoordinate worldCoordinate = new(x, z);

        try
        {
            int height = mod.GetBlockColumnHeight(worldCoordinate);
            return TextCommandResult.Success($"Terrain height at ({x}, {z}): {height}");
        }
        catch (Exception ex)
        {
            return TextCommandResult.Error($"Failed to sample terrain height: {ex.Message}");
        }
    }

    public TextCommandResult CmdSampleColumn(TextCommandCallingArgs args)
    {
        EntityPos playerPosition = args.Caller.Player.Entity.Pos;

        int x = (int)playerPosition.X;
        int z = (int)playerPosition.Z;

        TerrainSamplerGenTerra genTerra = mod.GenTerra;
        if (genTerra == null)
            return TextCommandResult.Error("TerrainSamplerGenTerra is not initialized.");

        try
        {
            TerrainColumnSample sample = mod.SampleColumn(x, z);

            int climateR = (sample.ClimateColor >> 16) & 0xFF;
            int climateG = (sample.ClimateColor >> 8) & 0xFF;
            int climateB = sample.ClimateColor & 0xFF;

            int worldHeight = genTerra.MapSizeY;

            StringBuilder sb = new();
            _ = sb.AppendLine($"Terrain column sample at (X={x}, Z={z}):")
                .AppendLine($"  Height: {sample.Height}/{worldHeight}")
                .AppendLine($"  ClimateColor: 0x{sample.ClimateColor:X8} (R/raw temperature:{climateR}/255, G/raw rainfall:{climateG}/255, B/raw geo activity:{climateB}/255)")
                .AppendLine($"  Temperature: {sample.Temperature:F4}/1")
                .AppendLine($"  Rainfall: {sample.Rainfall:F4}/1")
                .AppendLine($"  ForestDensity: {sample.ForestDensity:F4}/1")
                .AppendLine($"  ShrubDensity: {sample.ShrubDensity:F4}/1");

            return TextCommandResult.Success(sb.ToString());
        }
        catch (Exception ex)
        {
            return TextCommandResult.Error($"Failed to sample terrain column at ({x}, {z}): {ex.Message}");
        }
    }

#if DEBUG
    public TextCommandResult CmdBlockColumnTerrainHeightInfo(TextCommandCallingArgs args)
    {
        if (mod.WatershedsLoaded)
            return TextCommandResult.Error("Watersheds is enabled, use the watersheds terrain debug command instead");

        EntityPos playerPosition = args.Caller.Player.Entity.Pos;
        int playerX = (int)playerPosition.X;
        int playerZ = (int)playerPosition.Z;
        int playerY = (int)playerPosition.Y;

        TerrainSamplerGenTerra genTerra = mod.GenTerra;
        if (genTerra == null)
            return TextCommandResult.Error("TerrainSamplerGenTerra is not initialized.");

        WorldMapCoordinate playerWorldCoordinate = new(playerX, playerZ);
        SmallChunkMapCoordinate currentChunkCoordinate = playerWorldCoordinate.ToChunk();
        BlockInChunkMapCoordinate blockColumnInChunkCoordinate = playerWorldCoordinate.ToBlockInChunk();

        TerrainGenerationContext context = CreateTerrainGenerationContext(genTerra, currentChunkCoordinate, includeClimate: true);

        (VectorXZ terrainDistortion, float heightDistortion, float oceanDistortion) = genTerra.CreateDistortionEffectsForColumn(
            playerWorldCoordinate, blockColumnInChunkCoordinate, context.UpheavalMapCorners, context.OceanMapCorners);

        TerrainSamplerNormalizedSimplexFractalNoise.ColumnNoise columnNoise = genTerra.CreateColumnNoise(
            blockColumnInChunkCoordinate, context.TerrainOctaves, playerWorldCoordinate, terrainDistortion);

        int columnHeight = mod.GetBlockColumnHeight(playerWorldCoordinate);

        int regionChunkSize = genTerra.RegionChunkSize;
        ChunkInRegionMapCoordinate chunkInRegionCoordinate = currentChunkCoordinate.ToChunkInRegion(regionChunkSize);
        RegionMapCoordinate regionCoordinate = currentChunkCoordinate.ToRegion(regionChunkSize);

        float chunkPixelBlockStep = context.ChunkPixelSize * context.ChunkBlockDelta;
        float landformMapX = context.RegionMapChunkStartCoordinate.X + (blockColumnInChunkCoordinate.X * chunkPixelBlockStep);
        float landformMapZ = context.RegionMapChunkStartCoordinate.Z + (blockColumnInChunkCoordinate.Z * chunkPixelBlockStep);

        WeightedIndex[] columnWeightedIndices = context.LandLerpMap[landformMapX, landformMapZ];

        StartSampleDisplacedYThreshold(playerY + heightDistortion, genTerra.MapSizeY - 2, out int distortedPosYBase, out float distortedPosYSlide);

        double threshold = SumLandformThresholdAt(distortedPosYBase, distortedPosYSlide, columnWeightedIndices, genTerra.Landforms);

        double inverseCurvedThreshold = -NormalizedSimplexNoise.NoiseValueCurveInverse(threshold);
        NoiseSignDiagnostics diagnostics = columnNoise.NoiseSignWithDebugDiag(playerY, inverseCurvedThreshold);

        StringBuilder sb = new();
        _ = sb.AppendLine($"Terrain at X:{playerX}, Z:{playerZ}")
            .AppendLine($"columnHeight: {columnHeight}, Player Y: {playerY}")
            .AppendLine($"regionCoordinate: X:{regionCoordinate.X}, Z:{regionCoordinate.Z}")
            .AppendLine($"chunkInRegionCoordinate: X:{chunkInRegionCoordinate.X}, Z:{chunkInRegionCoordinate.Z}")
            .AppendLine($"blockColumnInChunkCoordinate: X:{blockColumnInChunkCoordinate.X}, Z:{blockColumnInChunkCoordinate.Z}")
            .AppendLine($"LandformMap Sample: X:{landformMapX:F4}, Z:{landformMapZ:F4}")
            .AppendLine("Landform Weights:");

        for (int i = 0; i < columnWeightedIndices.Length; i++)
        {
            if (columnWeightedIndices[i].Weight > 0.001f)
            {
                int landformIndex = columnWeightedIndices[i].Index;
                string landformCode = genTerra.Landforms.LandFormsByIndex[landformIndex].Code.Path;
                _ = sb.AppendLine($"  {landformCode}: {columnWeightedIndices[i].Weight:F4} (index {landformIndex})");
            }
        }

        _ = sb.AppendLine($"UpheavalMapCorners: " +
            $"UL={context.UpheavalMapCorners.UpLeft}, UR={context.UpheavalMapCorners.UpRight}, " +
            $"BL={context.UpheavalMapCorners.BotLeft}, BR={context.UpheavalMapCorners.BotRight}")
            .AppendLine($"OceanMapCorners: UL={context.OceanMapCorners.UpLeft}, UR={context.OceanMapCorners.UpRight}, " +
            $"BL={context.OceanMapCorners.BotLeft}, BR={context.OceanMapCorners.BotRight}");

        if (!context.ClimateMapCorners.IsEmpty)
        {
            _ = sb.AppendLine($"ClimateMapCorners: UL={context.ClimateMapCorners.UpLeft}, UR={context.ClimateMapCorners.UpRight}, " +
                $"BL={context.ClimateMapCorners.BotLeft}, BR={context.ClimateMapCorners.BotRight}");
        }

        _ = sb.AppendLine($"heightDistortion: {heightDistortion:F4}")
            .AppendLine($"terrainDistortion: X:{terrainDistortion.X:F4}, Z:{terrainDistortion.Z:F4}")
            .AppendLine($"oceanDistortion: {oceanDistortion:F4}")
            .AppendLine($"threshold at Y: {threshold:F6}")
            .AppendLine($"inverseCurvedThreshold: {inverseCurvedThreshold:F6}")
            .AppendLine($"FinalNoiseSign: {diagnostics.FinalNoiseSign:F6}")
            .AppendLine($"IsSolid: {diagnostics.IsSolid}")
            .AppendLine($"Octaves (at Y={playerY}):");

        for (int i = 0; i < diagnostics.OctaveDetails.Length; i++)
        {
            OctaveDiagnostic octave = diagnostics.OctaveDetails[i];
            _ = sb.AppendLine($"  [{octave.OctaveIndex}] " +
                $"RawNoiseValue={octave.RawNoiseValue:F6} ({(octave.RawNoiseValue > 0 ? "mound" : "valley")}), " +
                $"Amplitude={octave.Amplitude:F6}, " +
                $"Threshold={octave.Threshold:F6}, " +
                $"SmoothingFactor={octave.SmoothingFactor:F6}, " +
                $"StopBound={octave.StopBound:F6}, " +
                $"Contribution={octave.Contribution:F6}, " +
                $"AccumulatedValueBefore={octave.AccumulatedValueBefore:F6}, " +
                $"EarlyExitTriggered={octave.EarlyExitTriggered}");
        }

        return TextCommandResult.Success(sb.ToString());
    }
#endif
}
