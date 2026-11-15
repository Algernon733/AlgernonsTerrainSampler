using System.Diagnostics;
using AlgernonsTerrainSampler.Tiles;

namespace AlgernonsTerrainSampler.Tests;

/// <summary>Smooth, deterministic terrain standing in for the sampler, counting every column it is asked for.</summary>
internal sealed class FakeTerrain
{
    private long samples;

    public long Samples => Interlocked.Read(ref this.samples);

    /// <summary>When set, a column inside this tile throws, as a broken generator would.</summary>
    public TerrainTileKey? FailIn { get; set; }

    /// <summary>When set, every column throws, as a sampler whose generator has gone would.</summary>
    public volatile bool FailAll;

    /// <summary>When set, a column it is true for throws, as an area that cannot be sampled would.</summary>
    public Func<int, int, bool>? FailWhere { get; set; }

    /// <summary>When set, columns are noise, so a tile hardly compresses: some 180 KiB, where smooth ground is a few.</summary>
    public bool Noisy { get; init; }

    public TerrainColumnSample Sample(int x, int z)
    {
        _ = Interlocked.Increment(ref this.samples);
        if (this.FailAll || this.FailWhere?.Invoke(x, z) == true
            || (this.FailIn is TerrainTileKey failing && x >= failing.OriginX && x <= failing.OriginX + failing.Span && z >= failing.OriginZ && z <= failing.OriginZ + failing.Span))
            throw new InvalidOperationException("broken generator");

        return this.Noisy ? NoisyColumn(x, z) : Column(x, z);
    }

    public static TerrainColumnSample NoisyColumn(int x, int z)
    {
        uint a = Mix((uint)x * 0x9E3779B1u ^ (uint)z * 0x85EBCA77u);
        uint b = Mix(a ^ 0xC2B2AE3Du);
        return new TerrainColumnSample
        {
            Height = 1 + (int)(a % 250),
            ClimateColor = (int)(b & 0xFFFFFF),
            Rainfall = (a >> 8 & 0xFF) / 255f,
            Temperature = (b >> 24) / 255f,
            ForestDensity = (a >> 16) / 65535f,
            ShrubDensity = (b & 0xFFFF) / 65535f
        };

        static uint Mix(uint h)
        {
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            return h ^ (h >> 16);
        }
    }

    public static TerrainColumnSample Column(int x, int z) => new()
    {
        Height = 110 + (int)(40 * Math.Sin(x / 97.0) * Math.Cos(z / 131.0)) + ((x ^ z) & 3),
        ClimateColor = (((x / 64) & 0xFF) << 16) | (((z / 64) & 0xFF) << 8) | ((x + z) & 0x3F),
        Rainfall = ((x / 32) & 0xFF) / 255f,
        Temperature = ((x / 64) & 0xFF) / 255f,
        ForestDensity = (float)(0.5 + (0.5 * Math.Sin((x + z) / 211.0))),
        ShrubDensity = (float)(0.25 + (0.25 * Math.Cos((x - z) / 173.0)))
    };
}

internal static class Protobuf
{
    /// <summary>The message after a trip through protobuf, as the channel would carry it.</summary>
    public static T Wire<T>(T message)
    {
        using MemoryStream stream = new();
        ProtoBuf.Serializer.Serialize(stream, message);
        stream.Position = 0;
        return ProtoBuf.Serializer.Deserialize<T>(stream);
    }
}

internal static class Wait
{
    /// <summary>Calls <paramref name="tick"/> until <paramref name="done"/>, or fails after twenty seconds.</summary>
    public static void Until(Func<bool> done, Action tick, string what)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!done())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20))
                Assert.Fail($"Timed out waiting for {what}.");

            tick();
            Thread.Sleep(1);
        }
    }
}

/// <summary>A folder of its own for one test, deleted after it.</summary>
internal sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder()
    {
        // Beside the test binaries rather than in the system temp folder.
        this.Path = System.IO.Path.Combine(AppContext.BaseDirectory, "test-temp", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(this.Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
