#nullable enable
using System;
using System.Collections.Generic;
using ProtoBuf;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>The tile channel's protocol. A tile's key names a request, so there are no request ids.</summary>
internal static class TerrainTileProtocol
{
    public const string ChannelName = "algernonsterrainsampler";

    /// <summary>Bump on any change to the messages below; a client and server that differ say so and do nothing.</summary>
    public const int Version = 1;

    /// <summary>Largest piece of a tile in one packet.</summary>
    public const int FragmentBytes = 32 * 1024;

    /// <summary>Largest tile either side accepts: far above any real tile, so a corrupt length cannot exhaust memory.</summary>
    public const int MaxTileBytes = 1024 * 1024;

    /// <summary>Most keys in one want message, and in one refusal.</summary>
    public const int MaxKeysPerWant = 4096;

    /// <summary>
    /// Most tiles the server sends a client before the client says it has them: the flow control that keeps a slow
    /// client's gameplay packets from queuing behind tile data, and the server's memory from growing with its backlog.
    /// </summary>
    public const int WindowTiles = 2;
}

/// <summary>Client to server, when its channel connects: which protocol it speaks.</summary>
[ProtoContract]
public sealed class TerrainTileHello
{
    [ProtoMember(1)] public int Protocol { get; set; } = TerrainTileProtocol.Version;
}

/// <summary>Server to client, in answer to a hello: whether it serves tiles, and on what terms.</summary>
[ProtoContract]
public sealed class TerrainTileStatus
{
    [ProtoMember(1)] public int Protocol { get; set; } = TerrainTileProtocol.Version;
    [ProtoMember(2)] public bool Available { get; set; }
    [ProtoMember(3)] public string Reason { get; set; } = "";
    [ProtoMember(4)] public string Identity { get; set; } = "";
    [ProtoMember(5)] public int Cells { get; set; } = TerrainTileKey.Cells;
    [ProtoMember(6)] public int MinStep { get; set; } = TerrainTileKey.MinStep;
    [ProtoMember(7)] public int MaxStep { get; set; } = TerrainTileKey.MaxStep;
    [ProtoMember(8)] public int MaxWantedTiles { get; set; }
    [ProtoMember(9)] public int MaxDistancePerStep { get; set; }
    [ProtoMember(10)] public int MaxDistance { get; set; }
}

/// <summary>
/// Client to server: changes to the set of tiles it wants. The server keeps that set while the client stays connected
/// and forgets it when the client leaves, so a client that reconnects simply asks again. <see cref="Reset"/> empties the
/// set first, for a client that would rather replace it whole than drop its tiles one by one; this mod's client never
/// needs to. Adding a key that is already wanted, or already on its way, changes its priority and nothing more.
/// </summary>
[ProtoContract]
public sealed class TerrainTileWant
{
    [ProtoMember(1)] public bool Reset { get; set; }
    [ProtoMember(2, IsPacked = true)] public int[]? AddStep { get; set; }
    [ProtoMember(3, IsPacked = true)] public int[]? AddX { get; set; }
    [ProtoMember(4, IsPacked = true)] public int[]? AddZ { get; set; }
    [ProtoMember(5, IsPacked = true)] public int[]? AddPriority { get; set; }
    [ProtoMember(6, IsPacked = true)] public int[]? DropStep { get; set; }
    [ProtoMember(7, IsPacked = true)] public int[]? DropX { get; set; }
    [ProtoMember(8, IsPacked = true)] public int[]? DropZ { get; set; }

    internal int AddCount => Math.Min(Math.Min(this.AddStep?.Length ?? 0, this.AddX?.Length ?? 0), Math.Min(this.AddZ?.Length ?? 0, this.AddPriority?.Length ?? 0));

    internal int DropCount => Math.Min(this.DropStep?.Length ?? 0, Math.Min(this.DropX?.Length ?? 0, this.DropZ?.Length ?? 0));

    /// <summary>The tiles added, each once at its first priority, at most <see cref="TerrainTileProtocol.MaxKeysPerWant"/> of them.</summary>
    internal List<(TerrainTileKey Key, int Priority)> Adds()
    {
        int count = Math.Min(this.AddCount, TerrainTileProtocol.MaxKeysPerWant);
        HashSet<TerrainTileKey> seen = [];
        List<(TerrainTileKey Key, int Priority)> adds = [];
        for (int i = 0; i < count; i++)
        {
            TerrainTileKey key = new(this.AddStep![i], this.AddX![i], this.AddZ![i]);
            if (seen.Add(key))
                adds.Add((key, this.AddPriority![i]));
        }

        return adds;
    }

    /// <summary>The tiles dropped, at most <see cref="TerrainTileProtocol.MaxKeysPerWant"/> of them.</summary>
    internal List<TerrainTileKey> Drops()
    {
        int count = Math.Min(this.DropCount, TerrainTileProtocol.MaxKeysPerWant);
        List<TerrainTileKey> drops = new(count);
        for (int i = 0; i < count; i++)
            drops.Add(new TerrainTileKey(this.DropStep![i], this.DropX![i], this.DropZ![i]));

        return drops;
    }
}

/// <summary>Server to client, every few seconds while it waits on tiles: that the server is working, and how much is left.</summary>
[ProtoContract]
public sealed class TerrainTileProgress
{
    [ProtoMember(1)] public int Wanted { get; set; }
    [ProtoMember(2)] public int Sending { get; set; }
    [ProtoMember(3)] public int Making { get; set; }
    [ProtoMember(4)] public int Waiting { get; set; }
}

/// <summary>
/// Client to server: the last piece of a tile has arrived, whether the client kept the tile or not. The server sends no
/// more than <see cref="TerrainTileProtocol.WindowTiles"/> tiles it has not heard this for.
/// </summary>
[ProtoContract]
public sealed class TerrainTileReceived
{
    [ProtoMember(1)] public int Step { get; set; }
    [ProtoMember(2)] public int TileX { get; set; }
    [ProtoMember(3)] public int TileZ { get; set; }
}

/// <summary>Server to client: one piece of an encoded tile. The pieces of one tile arrive in order.</summary>
[ProtoContract]
public sealed class TerrainTileData
{
    [ProtoMember(1)] public int Step { get; set; }
    [ProtoMember(2)] public int TileX { get; set; }
    [ProtoMember(3)] public int TileZ { get; set; }
    [ProtoMember(4)] public int Total { get; set; }
    [ProtoMember(5)] public int Offset { get; set; }
    [ProtoMember(6)] public byte[]? Data { get; set; }
}

/// <summary>
/// Server to client: tiles it will not send, all for one reason, at most <see cref="TerrainTileProtocol.MaxKeysPerWant"/>
/// of them. A key refused may be outside the world, so its indices are zigzag coded, as cheap negative as positive.
/// </summary>
[ProtoContract]
public sealed class TerrainTileRefused
{
    [ProtoMember(1, IsPacked = true)] public int[]? Step { get; set; }
    [ProtoMember(2, IsPacked = true, DataFormat = DataFormat.ZigZag)] public int[]? TileX { get; set; }
    [ProtoMember(3, IsPacked = true, DataFormat = DataFormat.ZigZag)] public int[]? TileZ { get; set; }
    [ProtoMember(4)] public string Reason { get; set; } = "";

    internal int Count => Math.Min(this.Step?.Length ?? 0, Math.Min(this.TileX?.Length ?? 0, this.TileZ?.Length ?? 0));

    internal static TerrainTileRefused For(IReadOnlyList<TerrainTileKey> keys, string reason)
    {
        TerrainTileRefused refused = new() { Step = new int[keys.Count], TileX = new int[keys.Count], TileZ = new int[keys.Count], Reason = reason };
        for (int i = 0; i < keys.Count; i++)
        {
            refused.Step[i] = keys[i].Step;
            refused.TileX[i] = keys[i].TileX;
            refused.TileZ[i] = keys[i].TileZ;
        }

        return refused;
    }

    internal List<TerrainTileKey> Keys()
    {
        int count = Math.Min(this.Count, TerrainTileProtocol.MaxKeysPerWant);
        List<TerrainTileKey> keys = new(count);
        for (int i = 0; i < count; i++)
            keys.Add(new TerrainTileKey(this.Step![i], this.TileX![i], this.TileZ![i]));

        return keys;
    }
}
