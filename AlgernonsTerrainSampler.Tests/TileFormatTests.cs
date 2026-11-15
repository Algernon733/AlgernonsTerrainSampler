using System.IO;
using AlgernonsTerrainSampler.Tiles;

namespace AlgernonsTerrainSampler.Tests;

public class TileFormatTests
{
    private static TerrainTile Make(TerrainTileKey key)
    {
        TerrainTile tile = new(key);
        for (int iz = 0; iz < TerrainTileKey.Samples; iz++)
        {
            for (int ix = 0; ix < TerrainTileKey.Samples; ix++)
                tile.Set(TerrainTile.Index(ix, iz), FakeTerrain.Column(key.SampleX(ix), key.SampleZ(iz)));
        }

        return tile;
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 0, 128)]
    [InlineData(4, 3, 5, 1536, 2560, 512)]
    [InlineData(64, 1, 2, 8192, 16384, 8192)]
    public void A_tile_spans_its_cells_times_its_step_and_is_aligned_to_that(int step, int x, int z, int originX, int originZ, int span)
    {
        TerrainTileKey key = new(step, x, z);
        Assert.Equal(originX, key.OriginX);
        Assert.Equal(originZ, key.OriginZ);
        Assert.Equal(span, key.Span);
        Assert.Equal(key, TerrainTileKey.Containing(step, key.OriginX + key.Span - 1, key.OriginZ));
        Assert.Equal(key.TileX + 1, TerrainTileKey.Containing(step, key.OriginX + key.Span, key.OriginZ).TileX);
    }

    [Fact]
    public void Neighbouring_tiles_share_their_edge_columns()
    {
        TerrainTileKey left = new(4, 7, 9);
        TerrainTileKey right = new(4, 8, 9);
        Assert.Equal(left.SampleX(TerrainTileKey.Cells), right.SampleX(0));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(128, false)]
    [InlineData(1, true)]
    [InlineData(16, true)]
    [InlineData(64, true)]
    public void Steps_are_powers_of_two_from_1_to_64(int step, bool valid) => Assert.Equal(valid, TerrainTileKey.IsValidStep(step));

    [Fact]
    public void Tiles_outside_the_world_are_not_valid()
    {
        Assert.True(new TerrainTileKey(4, 0, 0).IsValid(1024000, 1024000));
        Assert.False(new TerrainTileKey(4, -1, 0).IsValid(1024000, 1024000));
        Assert.False(new TerrainTileKey(4, 2000, 0).IsValid(1024000, 1024000));
    }

    [Fact]
    public void A_tile_survives_encoding_exactly_and_compresses()
    {
        TerrainTileKey key = new(4, 1000, 1001);
        TerrainTile tile = Make(key);
        byte[] encoded = TerrainTileCodec.Encode(tile);
        TerrainTile decoded = TerrainTileCodec.Decode(encoded, key);

        Assert.Equal(tile.Height, decoded.Height);
        Assert.Equal(tile.ClimateR, decoded.ClimateR);
        Assert.Equal(tile.ClimateG, decoded.ClimateG);
        Assert.Equal(tile.ClimateB, decoded.ClimateB);
        Assert.Equal(tile.Rainfall, decoded.Rainfall);
        Assert.Equal(tile.Forest, decoded.Forest);
        Assert.Equal(tile.Shrub, decoded.Shrub);
        Assert.True(encoded.Length < TerrainTileKey.ColumnCount * 10 / 2, $"{encoded.Length} bytes is not under half the raw size.");
    }

    [Fact]
    public void A_column_reads_back_as_the_sampler_gave_it_to_the_quantisation()
    {
        TerrainColumnSample column = FakeTerrain.Column(5123, 7777);
        TerrainTile tile = new(new TerrainTileKey(1, 0, 0));
        tile.Set(0, column);
        TerrainColumnSample back = tile.Column(0);

        Assert.Equal(column.Height, back.Height);
        Assert.Equal(column.ClimateColor, back.ClimateColor);
        Assert.Equal(column.Rainfall, back.Rainfall, 1f / 255f);
        Assert.Equal(column.ForestDensity, back.ForestDensity, 1f / 65535f);
        Assert.Equal(column.ShrubDensity, back.ShrubDensity, 1f / 65535f);
    }

    [Fact]
    public void Damage_anywhere_or_the_wrong_name_is_caught()
    {
        TerrainTileKey key = new(8, 2, 3);
        byte[] encoded = TerrainTileCodec.Encode(Make(key));
        Assert.True(TerrainTileCodec.Verify(encoded, key));
        Assert.False(TerrainTileCodec.Verify(encoded, new TerrainTileKey(8, 2, 4)));

        byte[] payload = (byte[])encoded.Clone();
        payload[^10] ^= 0x40;
        Assert.False(TerrainTileCodec.Verify(payload, key));
        _ = Assert.Throws<InvalidDataException>(() => TerrainTileCodec.Decode(payload, key));

        byte[] header = (byte[])encoded.Clone();
        header[18] ^= 1;
        Assert.False(TerrainTileCodec.Verify(header, key));

        Assert.False(TerrainTileCodec.Verify(encoded.AsSpan(0, 40), key));
    }

    [Fact]
    public void The_identity_is_a_lower_case_sha256()
    {
        string identity = TerrainTileIdentity.Hash(writer => writer.Write("world"));
        Assert.True(TerrainTileIdentity.IsValid(identity));
        Assert.Equal(identity, TerrainTileIdentity.Hash(writer => writer.Write("world")));
        Assert.NotEqual(identity, TerrainTileIdentity.Hash(writer => writer.Write("world2")));
        Assert.False(TerrainTileIdentity.IsValid(identity.ToUpperInvariant()));
        Assert.False(TerrainTileIdentity.IsValid("../" + identity[3..]));
    }

    [Fact]
    public void The_canary_samples_four_regions_inside_the_world()
    {
        (int X, int Z)[] columns = TerrainTileIdentity.CanaryColumns(512000, 512000, 512, 1024000, 1024000);
        Assert.Equal(64, columns.Length);
        Assert.Equal(64, columns.Distinct().Count());
        Assert.Equal(4, columns.Select(c => (c.X / 512, c.Z / 512)).Distinct().Count());
    }
}
