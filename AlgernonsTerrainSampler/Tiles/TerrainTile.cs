#nullable enable
using System;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>
/// One tile of point samples, <see cref="TerrainTileKey.Samples"/> by <see cref="TerrainTileKey.Samples"/>, row major
/// (index <c>iz * 129 + ix</c>). Sample (ix, iz) is the world column (<c>OriginX + ix * Step</c>, <c>OriginZ + iz * Step</c>),
/// clamped to the world.
/// </summary>
/// <remarks>
/// Read only: one instance is handed to every consumer that asked for the tile. The fields are public so a mod without
/// a compile-time reference to the sampler can read them through cached <c>FieldInfo</c>s.
/// </remarks>
public sealed class TerrainTile
{
    public readonly int Step;
    public readonly int TileX;
    public readonly int TileZ;

    /// <summary>The terrain surface height, as <see cref="TerrainColumnSample.Height"/>.</summary>
    public readonly short[] Height;

    /// <summary>The raw climate map's red channel: temperature, 0 to 255.</summary>
    public readonly byte[] ClimateR;

    /// <summary>The raw climate map's green channel: rainfall, 0 to 255.</summary>
    public readonly byte[] ClimateG;

    /// <summary>The raw climate map's blue channel: geologic activity, 0 to 255.</summary>
    public readonly byte[] ClimateB;

    /// <summary>Height-adjusted rainfall, 0 to 255: <see cref="TerrainColumnSample.Rainfall"/> times 255.</summary>
    public readonly byte[] Rainfall;

    /// <summary>Forest density, 0 to 65535: <see cref="TerrainColumnSample.ForestDensity"/> times 65535.</summary>
    public readonly ushort[] Forest;

    /// <summary>Shrub density, 0 to 65535: <see cref="TerrainColumnSample.ShrubDensity"/> times 65535.</summary>
    public readonly ushort[] Shrub;

    public TerrainTile(TerrainTileKey key)
    {
        this.Step = key.Step;
        this.TileX = key.TileX;
        this.TileZ = key.TileZ;
        this.Height = new short[TerrainTileKey.ColumnCount];
        this.ClimateR = new byte[TerrainTileKey.ColumnCount];
        this.ClimateG = new byte[TerrainTileKey.ColumnCount];
        this.ClimateB = new byte[TerrainTileKey.ColumnCount];
        this.Rainfall = new byte[TerrainTileKey.ColumnCount];
        this.Forest = new ushort[TerrainTileKey.ColumnCount];
        this.Shrub = new ushort[TerrainTileKey.ColumnCount];
    }

    public TerrainTileKey Key => new(this.Step, this.TileX, this.TileZ);

    public static int Index(int ix, int iz) => (iz * TerrainTileKey.Samples) + ix;

    /// <summary>The packed climate colour, as <see cref="TerrainColumnSample.ClimateColor"/>.</summary>
    public int ClimateColor(int index) => (this.ClimateR[index] << 16) | (this.ClimateG[index] << 8) | this.ClimateB[index];

    /// <summary>The column as the sampler's own struct, for code written against <see cref="TerrainColumnSample"/>.</summary>
    public TerrainColumnSample Column(int index) => new()
    {
        Height = this.Height[index],
        ClimateColor = this.ClimateColor(index),
        Rainfall = this.Rainfall[index] / 255f,
        Temperature = this.ClimateR[index] / 255f,
        ForestDensity = this.Forest[index] / 65535f,
        ShrubDensity = this.Shrub[index] / 65535f
    };

    internal void Set(int index, TerrainColumnSample sample)
    {
        this.Height[index] = (short)Math.Clamp(sample.Height, short.MinValue, short.MaxValue);
        this.ClimateR[index] = (byte)((sample.ClimateColor >> 16) & 0xFF);
        this.ClimateG[index] = (byte)((sample.ClimateColor >> 8) & 0xFF);
        this.ClimateB[index] = (byte)(sample.ClimateColor & 0xFF);
        this.Rainfall[index] = Quantise8(sample.Rainfall);
        this.Forest[index] = Quantise16(sample.ForestDensity);
        this.Shrub[index] = Quantise16(sample.ShrubDensity);
    }

    private static byte Quantise8(float value) => (byte)Math.Round(Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, 1f) * 255f);

    private static ushort Quantise16(float value) => (ushort)Math.Round(Math.Clamp(float.IsFinite(value) ? value : 0f, 0f, 1f) * 65535f);
}
