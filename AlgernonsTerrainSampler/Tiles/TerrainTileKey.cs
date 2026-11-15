#nullable enable
using System;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>
/// Names one tile of point samples. A tile is always <see cref="Cells"/> cells across, so <see cref="Samples"/> by
/// <see cref="Samples"/> columns, one every <see cref="Step"/> blocks. It spans <c>Cells * Step</c> blocks and is aligned
/// to that span. Its last row and column lie on the next tile's first, so neighbouring tiles agree along their edge.
/// </summary>
/// <remarks>
/// Every tile costs the same to sample whatever its step, which is what keeps the queue, the limits and the cache simple.
/// </remarks>
public readonly record struct TerrainTileKey(int Step, int TileX, int TileZ)
{
    /// <summary>Cells across a tile.</summary>
    public const int Cells = 128;

    /// <summary>Samples across a tile: one more than its cells, the last shared with the next tile.</summary>
    public const int Samples = Cells + 1;

    /// <summary>Columns in a tile.</summary>
    public const int ColumnCount = Samples * Samples;

    public const int MinStep = 1;
    public const int MaxStep = 64;

    /// <summary>Steps are powers of two from <see cref="MinStep"/> to <see cref="MaxStep"/>.</summary>
    public static bool IsValidStep(int step) => step >= MinStep && step <= MaxStep && (step & (step - 1)) == 0;

    /// <summary>Blocks across the tile.</summary>
    public int Span => Cells * this.Step;

    public int OriginX => this.TileX * this.Span;

    public int OriginZ => this.TileZ * this.Span;

    public int CentreX => this.OriginX + (this.Span / 2);

    public int CentreZ => this.OriginZ + (this.Span / 2);

    /// <summary>The world column sample <paramref name="ix"/>, before clamping to the world.</summary>
    public int SampleX(int ix) => this.OriginX + (ix * this.Step);

    /// <summary>The world column sample <paramref name="iz"/>, before clamping to the world.</summary>
    public int SampleZ(int iz) => this.OriginZ + (iz * this.Step);

    /// <summary>A valid step, and a tile that starts inside the world. Samples past the world's far edge are clamped to it.</summary>
    public bool IsValid(int mapSizeX, int mapSizeZ)
        => IsValidStep(this.Step) && this.TileX >= 0 && this.TileZ >= 0
            && (long)this.TileX * Cells * this.Step < mapSizeX && (long)this.TileZ * Cells * this.Step < mapSizeZ;

    /// <summary>The tile at <paramref name="step"/> holding the world column (<paramref name="worldX"/>, <paramref name="worldZ"/>).</summary>
    public static TerrainTileKey Containing(int step, int worldX, int worldZ)
    {
        if (!IsValidStep(step))
            throw new ArgumentOutOfRangeException(nameof(step), step, "A step is a power of two from 1 to 64.");

        int span = Cells * step;
        return new TerrainTileKey(step, (int)Math.Floor(worldX / (double)span), (int)Math.Floor(worldZ / (double)span));
    }

    /// <summary>Squared distance in blocks from a world position to the tile's centre.</summary>
    public double DistanceSquaredTo(double worldX, double worldZ)
    {
        double dx = this.CentreX - worldX;
        double dz = this.CentreZ - worldZ;
        return (dx * dx) + (dz * dz);
    }

    public override string ToString() => $"Tile(step {this.Step}, {this.TileX}, {this.TileZ})";
}
