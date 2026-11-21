#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>Works out a world's tile cache identity from the game: everything <see cref="TerrainTileIdentity"/> promises.</summary>
internal static class TerrainTileWorldIdentity
{
    public static string For(ICoreServerAPI api, TerrainSamplerMod mod, int cacheRevision, System.Func<int, int, TerrainColumnSample> sample)
    {
        string samplerVersion = api.ModLoader.GetMod("algernonsterrainsampler")?.Info?.Version ?? "unknown";
        string watershedsVersion = mod.WatershedsLoaded ? api.ModLoader.GetMod("watersheds")?.Info?.Version ?? "unknown" : "";
        byte[] landforms = Asset(api, "worldgen/landforms.json");
        byte[] rockStrata = Asset(api, "worldgen/rockstrata.json");
        byte[] provinces = Asset(api, "worldgen/geologicprovinces.json");
        string defaultRock = DefaultRock(api);
        string generators = api.ModLoader.GetModSystem<GenMapsSubstitutions.SamplerGenMaps>()?.Fingerprint() ?? "";

        // The running game's version, read at run time: GameVersion's constants would be the version this was built with.
        string gameVersion = typeof(GameVersion).GetField(nameof(GameVersion.OverallVersion))?.GetValue(null) as string ?? "";

        IWorldManagerAPI world = api.WorldManager;
        (int X, int Z)[] canary = TerrainTileIdentity.CanaryColumns(world.MapSizeX / 2, world.MapSizeZ / 2, world.RegionSize, world.MapSizeX, world.MapSizeZ);

        return TerrainTileIdentity.Hash(writer =>
        {
            writer.Write("algernonsterrainsampler-tiles");
            writer.Write(TerrainTileIdentity.AlgorithmRevision);
            writer.Write(TerrainTileCodec.FormatVersion);
            writer.Write(samplerVersion);
            writer.Write(gameVersion);
            writer.Write(api.World.SavegameIdentifier ?? "");
            // The world's seed, never WorldgenSeed: this identity goes to every client, and from game 1.23 the terrain's
            // seed is the server's secret, which a hash of it beside otherwise public inputs would give up to a search of
            // 2^32. The canary columns below change the identity whenever the terrain does.
            writer.Write(world.Seed);
            writer.Write(world.MapSizeX);
            writer.Write(world.MapSizeY);
            writer.Write(world.MapSizeZ);
            writer.Write(world.RegionSize);
            writer.Write(api.World.SeaLevel);
            WriteAttribute(writer, api.World.Config);
            writer.Write(landforms.Length);
            writer.Write(landforms);
            writer.Write(rockStrata.Length);
            writer.Write(rockStrata);
            writer.Write(provinces.Length);
            writer.Write(provinces);
            writer.Write(defaultRock);
            writer.Write(generators);
            WriteTerraGenConfig(writer);
            writer.Write(mod.WatershedsLoaded);
            writer.Write(mod.WatershedsDetailedSampling);
            writer.Write(watershedsVersion);
            writer.Write(cacheRevision);

            // The canary: whatever else changed what the sampler answers, these change with it.
            foreach ((int x, int z) in canary)
                TerrainTileIdentity.WriteSample(writer, sample(x, z));
        });
    }

    // The asset as mods have patched it.
    private static byte[] Asset(ICoreServerAPI api, string path) => api.Assets.TryGet(new AssetLocation("game", path))?.Data ?? [];

    private static string DefaultRock(ICoreServerAPI api)
    {
        int id = GlobalConfig.GetInstance(api)?.defaultRockId ?? 0;
        return id > 0 && id < api.World.Blocks.Count ? api.World.Blocks[id]?.Code?.ToString() ?? "" : "";
    }

    // The tree in key order, so the same configuration always hashes the same.
    private static void WriteAttribute(BinaryWriter writer, IAttribute attribute)
    {
        if (attribute == null)
        {
            writer.Write(-1);
            return;
        }

        writer.Write(attribute.GetAttributeId());
        if (attribute is ITreeAttribute tree)
        {
            writer.Write(tree.Count);
            foreach (System.Collections.Generic.KeyValuePair<string, IAttribute> item in tree.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.Write(item.Key);
                WriteAttribute(writer, item.Value);
            }
        }
        else
        {
            attribute.ToBytes(writer);
        }
    }

    // Every public static setting of the game's terrain generation, which the sampler reads as the game does. A code mod
    // that changes one changes the identity.
    private static void WriteTerraGenConfig(BinaryWriter writer)
    {
        foreach (FieldInfo field in typeof(TerraGenConfig).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.FieldType.IsPrimitive || f.FieldType == typeof(string))
                     .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            writer.Write(field.Name);
            writer.Write(Convert.ToString(field.GetValue(null), CultureInfo.InvariantCulture) ?? "");
        }
    }
}
