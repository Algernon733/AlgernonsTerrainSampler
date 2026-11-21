using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using AlgernonsTerrainSampler.GenMapsSubstitutions;
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
            TerrainColumnSample sample = mod.SampleColumnWithTopRock(x, z);

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
                .AppendLine($"  ShrubDensity: {sample.ShrubDensity:F4}/1")
                .AppendLine($"  TopRock: {(sample.TopRock.Length > 0 ? sample.TopRock : "(not known)")}");

            return TextCommandResult.Success(sb.ToString());
        }
        catch (Exception ex)
        {
            return TextCommandResult.Error($"Failed to sample terrain column at ({x}, {z}): {ex.Message}");
        }
    }

    /// <summary>
    /// The sampled top rock against what worldgen put in the generated chunks round the caller (<c>TopRockIdMap</c>, the
    /// rock under the block layers), every column, each asked twice: at the chunk's own terrain height, which tests the
    /// strata alone, and at the sampler's height, which is what consumers get.
    /// </summary>
    /// <remarks>
    /// Two more lines tell a mistake in the walk from a world made differently: the walk on the world's own saved maps of
    /// each region (vanilla's inputs exactly), and how many regions' saved maps differ from the ones the sampler makes from
    /// the seed (an older game version or a mod that changed them would).
    /// </remarks>
    public TextCommandResult CmdRockCheck(TextCommandCallingArgs args)
    {
        if (mod.Rocks == null)
            return TextCommandResult.Error("The sampler has no rock strata for this world (see the server log).");

        if (args.Caller.Entity?.Pos is not EntityPos position)
            return TextCommandResult.Error("Run it as a player: it checks the chunks round you.");

        ICoreServerAPI api = (ICoreServerAPI)args.Caller.Entity.Api;

        // At most 17 x 17 chunks, about 300,000 columns sampled on the main thread.
        int radius = Math.Clamp((int?)args[0] ?? 4, 0, 8);
        int centreX = (int)position.X / Mapping.CHUNK_SIZE;
        int centreZ = (int)position.Z / Mapping.CHUNK_SIZE;
        int columns = 0, strataMatch = 0, sampledMatch = 0, savedMatch = 0, savedColumns = 0, chunks = 0;
        Dictionary<string, int> misses = [];
        HashSet<(int, int)> regions = [];
        Dictionary<(int, int), RockStrataSampler.SavedRegion> savedRegions = [];
        int regionChunks = api.WorldManager.RegionSize / Mapping.CHUNK_SIZE;
        for (int cz = centreZ - radius; cz <= centreZ + radius; cz++)
        {
            for (int cx = centreX - radius; cx <= centreX + radius; cx++)
            {
                IMapChunk mapChunk = api.WorldManager.GetMapChunk(cx, cz);
                if (mapChunk?.TopRockIdMap == null || mapChunk.WorldGenTerrainHeightMap == null)
                    continue;

                chunks++;
                _ = regions.Add((cx / regionChunks, cz / regionChunks));
                (int, int) regionKey = (cx / regionChunks, cz / regionChunks);
                if (!savedRegions.TryGetValue(regionKey, out RockStrataSampler.SavedRegion saved))
                {
                    IMapRegion mapRegion = api.WorldManager.GetMapRegion(regionKey.Item1, regionKey.Item2);
                    saved = mapRegion == null ? null : RockStrataSampler.Saved(mapRegion);
                    savedRegions[regionKey] = saved;
                }
                for (int lz = 0; lz < Mapping.CHUNK_SIZE; lz++)
                {
                    for (int lx = 0; lx < Mapping.CHUNK_SIZE; lx++)
                    {
                        int at = (lz * Mapping.CHUNK_SIZE) + lx;
                        int rockId = mapChunk.TopRockIdMap[at];
                        if (rockId <= 0)
                            continue;

                        string actual = api.World.Blocks[rockId]?.Code?.ToString() ?? "";
                        int x = (cx * Mapping.CHUNK_SIZE) + lx;
                        int z = (cz * Mapping.CHUNK_SIZE) + lz;
                        string byStrata = mod.Rocks.TopRock(x, z, mapChunk.WorldGenTerrainHeightMap[at]);
                        string bySample = mod.SampleColumnWithTopRock(x, z).TopRock;
                        string bySaved = saved == null ? null : mod.Rocks.TopRockFromSaved(saved, x, z, mapChunk.WorldGenTerrainHeightMap[at]);
                        columns++;
                        strataMatch += byStrata == actual ? 1 : 0;
                        sampledMatch += bySample == actual ? 1 : 0;
                        if (bySaved != null)
                        {
                            savedColumns++;
                            savedMatch += bySaved == actual ? 1 : 0;
                        }
                        if (byStrata != actual)
                        {
                            string key = $"{byStrata} for {actual}";
                            misses[key] = (misses.TryGetValue(key, out int seen) ? seen : 0) + 1;
                        }
                    }
                }
            }
        }

        if (columns == 0)
            return TextCommandResult.Error("No generated chunks with a top rock map round you.");

        StringBuilder sb = new();
        _ = sb.AppendLine($"Top rock against {chunks} generated chunks, {columns} columns:")
            .AppendLine($"  at the chunks' own terrain height (the strata alone): {100.0 * strataMatch / columns:F1} %")
            .AppendLine($"  as sampled, at the sampler's height: {100.0 * sampledMatch / columns:F1} %")
            .AppendLine(savedColumns == 0
                ? "  with the world's own saved maps: no saved maps to read"
                : $"  with the world's own saved maps, at the chunks' height: {100.0 * savedMatch / savedColumns:F1} % of {savedColumns}");

        int strataDiffer = 0, provincesDiffer = 0, compared = 0;
        foreach ((int rx, int rz) in regions)
        {
            IMapRegion mapRegion = api.WorldManager.GetMapRegion(rx, rz);
            if (mapRegion == null)
                continue;

            (int differ, _, bool provinces) = mod.Rocks.CompareSaved(mapRegion, rx, rz);
            compared++;
            strataDiffer += differ > 0 ? 1 : 0;
            provincesDiffer += provinces ? 1 : 0;
        }

        _ = sb.AppendLine($"  saved maps against the seed's, {compared} map regions: strata differ in {strataDiffer}, provinces in {provincesDiffer}");
        foreach (KeyValuePair<string, int> miss in misses.OrderByDescending(m => m.Value).Take(5))
            _ = sb.AppendLine($"  {miss.Value} columns gave {miss.Key}");

        return TextCommandResult.Success(sb.ToString());
    }

    /// <summary>
    /// Compares the map regions round the caller that the world has generated with the sampler's own maps of them: the
    /// climate, upheaval, ocean, landform, forest and shrub maps the sampler reads. A region the game forced a landform or
    /// climate into (story structures) differs there, since the sampler does not apply those.
    /// </summary>
    public TextCommandResult CmdMapCheck(TextCommandCallingArgs args)
    {
        if (args.Caller.Entity?.Pos is not EntityPos position)
            return TextCommandResult.Error("Run it as a player: it checks the map regions round you.");

        TerrainSamplerGenTerra genTerra = mod.GenTerra;
        GenMapsSubstitutions.SamplerGenMaps maps = genTerra?.Maps;
        if (maps?.IsReady != true)
            return TextCommandResult.Error("The sampler's map generators are not ready for this world (see the server log).");

        ICoreServerAPI api = (ICoreServerAPI)args.Caller.Entity.Api;
        int radius = Math.Clamp((int?)args[0] ?? 1, 0, 4);
        int regionSize = api.WorldManager.RegionSize;
        int centreX = (int)position.X / regionSize;
        int centreZ = (int)position.Z / regionSize;
        string[] names = ["climate", "upheaval", "ocean", "landform", "forest", "shrub"];
        int[] same = new int[names.Length];
        int[] cells = new int[names.Length];
        int[] cellsDiffer = new int[names.Length];
        int regions = 0;
        for (int rz = centreZ - radius; rz <= centreZ + radius; rz++)
        {
            for (int rx = centreX - radius; rx <= centreX + radius; rx++)
            {
                IMapRegion saved = api.WorldManager.GetMapRegion(rx, rz);
                if (saved == null)
                    continue;

                regions++;
                TerrainSamplerGenTerra.RegionMaps ours = genTerra.MapsOfRegion(new RegionMapCoordinate(rx, rz), maps);
                IntDataMap2D[] mine = [ours.Climate, ours.Upheaval, ours.Ocean, ours.Landform, ours.Forest, ours.Shrub];
                IntDataMap2D[] theirs = [saved.ClimateMap, saved.UpheavelMap, saved.OceanMap, saved.LandformMap, saved.ForestMap, saved.ShrubMap];
                for (int i = 0; i < names.Length; i++)
                {
                    int[] a = mine[i]?.Data ?? [];
                    int[] b = theirs[i]?.Data ?? [];
                    int differ = a.Length == b.Length ? a.Where((value, at) => value != b[at]).Count() : Math.Max(a.Length, b.Length);
                    cells[i] += Math.Max(a.Length, b.Length);
                    cellsDiffer[i] += differ;
                    same[i] += differ == 0 ? 1 : 0;
                }
            }
        }

        if (regions == 0)
            return TextCommandResult.Error("No map region round you has been generated yet.");

        StringBuilder sb = new();
        _ = sb.AppendLine(maps.Describe());
        _ = sb.AppendLine($"The sampler's maps against the world's own, {regions} generated map regions:");
        for (int i = 0; i < names.Length; i++)
            _ = sb.AppendLine($"  {names[i]}: identical in {same[i]} of {regions} regions; {cellsDiffer[i]} of {cells[i]} cells differ");

        return TextCommandResult.Success(sb.ToString());
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
