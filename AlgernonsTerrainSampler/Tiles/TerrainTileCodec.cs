#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>
/// The tile format. The same bytes are written to disk and sent over the network, so a cached tile is served
/// without being decoded.
/// </summary>
/// <remarks>
/// <para>Header, <see cref="HeaderBytes"/> bytes, little endian: magic "ATS1", format version, step, tile X, tile Z, cells,
/// field mask, payload length, then the SHA-256 of the payload.</para>
/// <para>Payload: Brotli over the field planes, in field-mask order. Each value is stored as its difference from the
/// sample to its left, or above it at the start of a row, so smooth ground becomes long runs of small numbers.
/// Sixteen-bit planes are then split into a plane of low bytes and a plane of high bytes.</para>
/// <para>The top rock is a plane of palette indices after the others, then the palette itself: a count (0 for 256) and
/// that many block codes, each a length byte and its UTF-8.</para>
/// </remarks>
internal static class TerrainTileCodec
{
    public const int Magic = 0x31535441; // "ATS1"
    public const byte FormatVersion = 1;
    public const int HeaderBytes = 54;

    // Height, climate R, G and B, rainfall, forest, shrub, top rock.
    public const ushort AllFields = 0xFF;

    private const int Columns = TerrainTileKey.ColumnCount;

    // The planes; the rock palette follows them.
    private const int PlaneBytes = Columns * 11;

    // The most a palette can take: a count, then 256 codes of up to 255 bytes and their lengths.
    private const int MostPaletteBytes = 1 + (256 * 256);
    private const int BrotliQuality = 5;
    private const int BrotliWindow = 22;

    public static byte[] Encode(TerrainTile tile)
    {
        byte[] palette = EncodePalette(tile.RockCodes);
        int rawBytes = PlaneBytes + palette.Length;
        byte[] raw = new byte[rawBytes];
        Span<byte> planes = raw;
        int offset = 0;

        Write16(MemoryMarshal.Cast<short, ushort>(tile.Height.AsSpan()), planes, ref offset);
        Write8(tile.ClimateR, planes, ref offset);
        Write8(tile.ClimateG, planes, ref offset);
        Write8(tile.ClimateB, planes, ref offset);
        Write8(tile.Rainfall, planes, ref offset);
        Write16(tile.Forest, planes, ref offset);
        Write16(tile.Shrub, planes, ref offset);
        Write8(tile.Rock, planes, ref offset);
        palette.CopyTo(planes[offset..]);

        byte[] buffer = new byte[HeaderBytes + BrotliEncoder.GetMaxCompressedLength(rawBytes)];
        if (!BrotliEncoder.TryCompress(raw, buffer.AsSpan(HeaderBytes), out int written, BrotliQuality, BrotliWindow))
            throw new InvalidOperationException("Could not compress a terrain tile.");

        Span<byte> header = buffer.AsSpan(0, HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(header, Magic);
        header[4] = FormatVersion;
        header[5] = (byte)tile.Step;
        BinaryPrimitives.WriteInt32LittleEndian(header[6..], tile.TileX);
        BinaryPrimitives.WriteInt32LittleEndian(header[10..], tile.TileZ);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], TerrainTileKey.Cells);
        BinaryPrimitives.WriteUInt16LittleEndian(header[16..], AllFields);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], written);
        _ = SHA256.HashData(buffer.AsSpan(HeaderBytes, written), header[22..]);

        return buffer.AsSpan(0, HeaderBytes + written).ToArray();
    }

    /// <summary>Reads and checks the header alone, without the checksum: enough to know which tile the bytes claim to be.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> encoded, out TerrainTileKey key)
    {
        key = default;
        if (encoded.Length < HeaderBytes
            || BinaryPrimitives.ReadInt32LittleEndian(encoded) != Magic
            || encoded[4] != FormatVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(encoded[14..]) != TerrainTileKey.Cells
            || BinaryPrimitives.ReadUInt16LittleEndian(encoded[16..]) != AllFields
            || BinaryPrimitives.ReadInt32LittleEndian(encoded[18..]) != encoded.Length - HeaderBytes)
            return false;

        key = new TerrainTileKey(encoded[5], BinaryPrimitives.ReadInt32LittleEndian(encoded[6..]), BinaryPrimitives.ReadInt32LittleEndian(encoded[10..]));
        return TerrainTileKey.IsValidStep(key.Step);
    }

    /// <summary>The header is sound, names <paramref name="expected"/>, and the payload matches its checksum.</summary>
    public static bool Verify(ReadOnlySpan<byte> encoded, TerrainTileKey expected)
    {
        if (!TryReadHeader(encoded, out TerrainTileKey key) || key != expected)
            return false;

        Span<byte> hash = stackalloc byte[32];
        _ = SHA256.HashData(encoded[HeaderBytes..], hash);
        return hash.SequenceEqual(encoded.Slice(22, 32));
    }

    /// <summary>Checks and decodes a tile. Throws <see cref="InvalidDataException"/> on anything malformed.</summary>
    public static TerrainTile Decode(ReadOnlySpan<byte> encoded, TerrainTileKey expected)
    {
        if (!Verify(encoded, expected))
            throw new InvalidDataException($"Terrain tile data for {expected} is damaged or names another tile.");

        byte[] raw = new byte[PlaneBytes + MostPaletteBytes];
        if (!BrotliDecoder.TryDecompress(encoded[HeaderBytes..], raw, out int written) || written <= PlaneBytes)
            throw new InvalidDataException($"Terrain tile data for {expected} does not decompress to a whole tile.");

        TerrainTile tile = new(expected);
        ReadOnlySpan<byte> planes = raw;
        int offset = 0;
        Read16(planes, MemoryMarshal.Cast<short, ushort>(tile.Height.AsSpan()), ref offset);
        Read8(planes, tile.ClimateR, ref offset);
        Read8(planes, tile.ClimateG, ref offset);
        Read8(planes, tile.ClimateB, ref offset);
        Read8(planes, tile.Rainfall, ref offset);
        Read16(planes, tile.Forest, ref offset);
        Read16(planes, tile.Shrub, ref offset);
        Read8(planes, tile.Rock, ref offset);
        tile.RockCodes = DecodePalette(planes[offset..written], expected);
        foreach (byte index in tile.Rock)
        {
            if (index >= tile.RockCodes.Length)
                throw new InvalidDataException($"Terrain tile data for {expected} names a rock its palette does not have.");
        }

        return tile;
    }

    private static byte[] EncodePalette(string[] codes)
    {
        int count = Math.Min(codes.Length, 256);
        using MemoryStream stream = new();
        stream.WriteByte((byte)(count & 0xFF));
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(codes[i] ?? "");
            int length = Math.Min(bytes.Length, 255);
            stream.WriteByte((byte)length);
            stream.Write(bytes, 0, length);
        }

        return stream.ToArray();
    }

    // A count of 0 stands for 256, the most a byte index can reach.
    private static string[] DecodePalette(ReadOnlySpan<byte> bytes, TerrainTileKey expected)
    {
        int count = bytes[0] == 0 ? 256 : bytes[0];
        string[] codes = new string[count];
        int at = 1;
        for (int i = 0; i < count; i++)
        {
            if (at >= bytes.Length || at + 1 + bytes[at] > bytes.Length)
                throw new InvalidDataException($"Terrain tile data for {expected} has a damaged rock palette.");

            int length = bytes[at];
            codes[i] = System.Text.Encoding.UTF8.GetString(bytes.Slice(at + 1, length));
            at += 1 + length;
        }

        return codes;
    }

    // The prediction for sample i: its left neighbour, or the sample above it at the start of a row.
    private static int Previous(int i) => i % TerrainTileKey.Samples != 0 ? i - 1 : i - TerrainTileKey.Samples;

    private static void Write8(ReadOnlySpan<byte> values, Span<byte> planes, ref int offset)
    {
        Span<byte> plane = planes.Slice(offset, Columns);
        for (int i = 0; i < Columns; i++)
        {
            int previous = Previous(i);
            plane[i] = (byte)(values[i] - (previous >= 0 ? values[previous] : 0));
        }

        offset += Columns;
    }

    private static void Write16(ReadOnlySpan<ushort> values, Span<byte> planes, ref int offset)
    {
        Span<byte> low = planes.Slice(offset, Columns);
        Span<byte> high = planes.Slice(offset + Columns, Columns);
        for (int i = 0; i < Columns; i++)
        {
            int previous = Previous(i);
            ushort residual = (ushort)(values[i] - (previous >= 0 ? values[previous] : 0));
            low[i] = (byte)residual;
            high[i] = (byte)(residual >> 8);
        }

        offset += 2 * Columns;
    }

    private static void Read8(ReadOnlySpan<byte> planes, Span<byte> values, ref int offset)
    {
        ReadOnlySpan<byte> plane = planes.Slice(offset, Columns);
        for (int i = 0; i < Columns; i++)
        {
            int previous = Previous(i);
            values[i] = (byte)(plane[i] + (previous >= 0 ? values[previous] : 0));
        }

        offset += Columns;
    }

    private static void Read16(ReadOnlySpan<byte> planes, Span<ushort> values, ref int offset)
    {
        ReadOnlySpan<byte> low = planes.Slice(offset, Columns);
        ReadOnlySpan<byte> high = planes.Slice(offset + Columns, Columns);
        for (int i = 0; i < Columns; i++)
        {
            int previous = Previous(i);
            values[i] = (ushort)((low[i] | (high[i] << 8)) + (previous >= 0 ? values[previous] : 0));
        }

        offset += 2 * Columns;
    }
}
