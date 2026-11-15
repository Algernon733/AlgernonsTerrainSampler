#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>
/// The identity of a tile cache: a SHA-256 of everything that decides what the sampler answers. Two caches with the
/// same identity hold the same tiles; anything that would change a tile changes the identity, and so the folder.
/// </summary>
internal static class TerrainTileIdentity
{
    /// <summary>
    /// Bump whenever the sampler's output changes for the same inputs: a fix to the terrain arithmetic, a new field.
    /// Every cache, on servers and clients, then starts again.
    /// </summary>
    public const int AlgorithmRevision = 1;

    public static string Hash(Action<BinaryWriter> write)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            write(writer);

        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static bool IsValid(string? identity)
    {
        if (identity == null || identity.Length != 64)
            return false;

        foreach (char c in identity)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                return false;
        }

        return true;
    }

    /// <summary>The canary's columns: a 4 x 4 lattice in each of four map regions round <paramref name="centreX"/>, <paramref name="centreZ"/>.</summary>
    public static (int X, int Z)[] CanaryColumns(int centreX, int centreZ, int regionSize, int mapSizeX, int mapSizeZ)
    {
        (int X, int Z)[] columns = new (int, int)[64];
        int regionX = centreX / regionSize;
        int regionZ = centreZ / regionSize;
        int spacing = Math.Max(1, regionSize / 4);
        int n = 0;
        for (int region = 0; region < 4; region++)
        {
            int originX = (regionX + (region & 1)) * regionSize;
            int originZ = (regionZ + (region >> 1)) * regionSize;
            for (int j = 0; j < 4; j++)
            {
                for (int i = 0; i < 4; i++)
                {
                    columns[n++] = (Math.Clamp(originX + (spacing / 2) + (i * spacing), 0, mapSizeX - 1),
                                    Math.Clamp(originZ + (spacing / 2) + (j * spacing), 0, mapSizeZ - 1));
                }
            }
        }

        return columns;
    }

    public static void WriteSample(BinaryWriter writer, TerrainColumnSample sample)
    {
        writer.Write(sample.Height);
        writer.Write(sample.ClimateColor);
        writer.Write(sample.Rainfall);
        writer.Write(sample.Temperature);
        writer.Write(sample.ForestDensity);
        writer.Write(sample.ShrubDensity);
    }
}
