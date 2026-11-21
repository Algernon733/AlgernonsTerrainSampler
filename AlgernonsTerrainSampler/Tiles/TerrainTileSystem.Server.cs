#nullable enable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace AlgernonsTerrainSampler.Tiles;

/// <summary>The server's half: the service, its channel, its commands.</summary>
public sealed partial class TerrainTileSystem
{
    private ICoreServerAPI? sapi;
    private IServerNetworkChannel? serverChannel;
    private TerrainTileLocalSource? local;
    private TerrainTileService? service;
    private TerrainTileSender? sender;
    private long serverTickListener;
    private Process? process;
    private long lastTick;
    private long lastCpuSample;
    private double lastCpuMilliseconds;
    private double cpuPercent;
    private long lastStatsLog;
    private string serviceIdentity = "";
    private TerrainTileTickGuard serverTick = null!;
    private readonly System.Collections.Generic.HashSet<string> greeted = new(StringComparer.Ordinal);

    public override void StartServerSide(ICoreServerAPI api)
    {
        this.sapi = api;
        ServerInstance = this;
        this.config = LoadConfig(api, store: true);
        this.serverChannel = api.Network.RegisterChannel(TerrainTileProtocol.ChannelName)
            .RegisterMessageType<TerrainTileHello>()
            .RegisterMessageType<TerrainTileStatus>()
            .RegisterMessageType<TerrainTileWant>()
            .RegisterMessageType<TerrainTileProgress>()
            .RegisterMessageType<TerrainTileData>()
            .RegisterMessageType<TerrainTileRefused>()
            .RegisterMessageType<TerrainTileReceived>()
            .SetMessageHandler<TerrainTileHello>(this.OnHello)
            .SetMessageHandler<TerrainTileWant>(this.OnWant)
            .SetMessageHandler<TerrainTileReceived>((player, received) => this.sender!.OnReceived(player.PlayerUID, received));

        this.sender = new TerrainTileSender(
            (uid, data) => this.SendTo(uid, data),
            (uid, refused) => this.SendTo(uid, refused),
            (uid, progress) => this.SendTo(uid, progress),
            this.Log);

        // Requests from mods on the server are taken from now on: until the service starts they are refused, with why.
        this.local = new TerrainTileLocalSource(() => this.UnavailableReason, this.Log);
        this.requests = this.local.Requests;
        this.serverTick = new TerrainTileTickGuard("the terrain tile service's tick", message => api.Logger.Error("{0}", LogPrefix + message), this.Log);

        // The sampler finds the game's generators at WorldReady; RunGame is after it.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, this.StartService);
        api.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, this.StopService);
        api.Event.PlayerDisconnect += this.OnPlayerDisconnect;
        this.serverTickListener = api.Event.RegisterGameTickListener(this.OnServerTick, 50);
    }

    // The tile service is an extra: nothing in it may stop the server from starting.
    private void StartService()
    {
        try
        {
            this.TryStartService();
        }
        catch (Exception ex)
        {
            this.service?.Dispose();
            this.service = null;
            this.local!.Service = null;
            this.SetUnavailable($"The terrain tile service failed to start: {ex.GetBaseException().Message}");
            this.sapi!.Logger.Error("{0}", LogPrefix + "the terrain tile service failed to start; the server runs on without it: " + ex);
        }
    }

    private void TryStartService()
    {
        ICoreServerAPI api = this.sapi!;
        TerrainSamplerMod? mod = TerrainSamplerMod.Instance;
        if (mod?.GenTerra == null)
        {
            this.SetUnavailable("The terrain sampler did not start.");
            return;
        }

        if (!mod.GenTerra.IsReady)
        {
            this.SetUnavailable("This world's terrain is not one the sampler knows: only worlds of the standard type can be sampled.");
            api.Logger.Notification("{0}", LogPrefix + "terrain tile service off: " + this.UnavailableReason);
            return;
        }

        TerrainColumnSample Sample(int x, int z) => mod.SampleColumnOrThrow(new WorldMapCoordinate(x, z));

        string identity;
        try
        {
            identity = TerrainTileWorldIdentity.For(api, mod, this.config.CacheRevision, Sample);
        }
        catch (Exception ex)
        {
            this.SetUnavailable($"The sampler failed its first samples: {ex.GetBaseException().Message}");
            api.Logger.Error("{0}", LogPrefix + "terrain tile service off, the sampler failed its first samples: " + ex);
            return;
        }

        TerrainTileDiskCache? disk = null;
        if (this.config.DiskCacheMegabytes > 0)
        {
            // Every world's tiles under worlds/, one quota for them all, and a world unused for 90 days dropped at start;
            // a client's tiles from multiplayer servers are in remote/, under a quota of their own.
            string worlds = api.GetOrCreateDataPath(Path.Combine("ModData", "algernonsterrainsampler", "worlds"));
            string folder = api.GetOrCreateDataPath(Path.Combine("ModData", "algernonsterrainsampler", "worlds", SafeFolderName(api.World.SavegameIdentifier)));
            disk = new TerrainTileDiskCache(folder, identity, this.config.DiskCacheMegabytes, this.Log, sharedParent: worlds, unusedDays: UnusedCacheDays);
        }

        int workers = this.config.Workers > 0 ? this.config.Workers
            : api.Server.IsDedicated ? 1 : Math.Clamp(Environment.ProcessorCount - 4, 1, 4);

        // Watersheds has not been checked for thread safety, so with it the sampling stays on the main thread, a few
        // milliseconds a tick. Encoding and writing still run on the workers.
        bool mainThread = mod.WatershedsLoaded;
        this.service = new TerrainTileService(
            api.WorldManager.MapSizeX,
            api.WorldManager.MapSizeZ,
            Sample,
            disk,
            new TerrainTileService.Settings(workers, this.config.MemoryCacheMegabytes, this.config.MaxWantedTiles, this.config.MaxDistancePerStep, this.config.MaxDistance, mainThread),
            this.OwnerInfo,
            this.OnTileReady,
            this.OnTileRefused,
            this.Log,
            queue: this.sender);

        this.local!.Service = this.service;
        this.process = Process.GetCurrentProcess();
        this.lastCpuMilliseconds = this.process.TotalProcessorTime.TotalMilliseconds;
        this.lastTick = this.lastCpuSample = Environment.TickCount64;
        this.serviceIdentity = identity;
        this.SetAvailable();

        api.Logger.Notification("{0}", LogPrefix + string.Create(
            CultureInfo.InvariantCulture,
            $"terrain tile service ready: {workers} worker(s){(mainThread ? ", sampling on the main thread for Watersheds" : "")}, disk cache {(disk == null ? "off" : $"{this.config.DiskCacheMegabytes} MiB")}, network sampling {(this.config.NetworkSampling ? "on" : "off")}, identity {identity[..12]}."));
    }

    private void StopService()
    {
        if (this.service == null)
            return;

        this.service.Dispose();
        this.service = null;
        this.local!.Service = null;
        this.requests?.FailAll("The server is stopping.");
        this.process?.Dispose();
        this.process = null;
        this.SetUnavailable("The server is stopping.");
    }

    private TerrainTileOwner OwnerInfo(string owner)
    {
        if (owner == TerrainTileLocalSource.Owner)
            return TerrainTileOwner.Unlimited;

        if (!this.config.NetworkSampling)
            return TerrainTileOwner.Denied($"This server does not serve terrain tiles to clients: NetworkSampling is off in {TerrainTileConfig.FileName}.");

        if (this.sapi!.World.PlayerByUid(owner) is not IServerPlayer player)
            return TerrainTileOwner.Denied("Not connected.");

        if (!string.IsNullOrWhiteSpace(this.config.RequiredPrivilege) && !player.HasPrivilege(this.config.RequiredPrivilege))
            return TerrainTileOwner.Denied($"Terrain tiles on this server need the '{this.config.RequiredPrivilege}' privilege.");

        EntityPos position = player.Entity?.Pos ?? this.sapi.World.DefaultSpawnPosition;
        return new TerrainTileOwner(true, "", true, position?.X ?? 0, position?.Z ?? 0);
    }

    private void OnTileReady(string owner, TerrainTileKey key, byte[] data)
    {
        if (owner == TerrainTileLocalSource.Owner)
            this.local!.Ready(key, data);
        else
            this.sender!.Enqueue(owner, key, data);
    }

    private void OnTileRefused(string owner, TerrainTileKey key, string reason)
    {
        // A server mod hears on a later tick, never inside its own request.
        if (owner == TerrainTileLocalSource.Owner)
            this.local!.Refused(key, reason);
        else
            this.sender!.Refuse(owner, key, reason);
    }

    private void OnHello(IServerPlayer player, TerrainTileHello hello)
    {
        TerrainTileStatus status = hello.Protocol == TerrainTileProtocol.Version
            ? this.StatusFor(player)
            : this.Status(false, $"This server speaks tile protocol {TerrainTileProtocol.Version}, the client {hello.Protocol}. Use the same terrain sampler version on both.");

        // Once a connection: whether a player's client found the service, and what it was told. Those who said hello
        // hear again whenever the service goes or comes back.
        if (this.greeted.Add(player.PlayerUID))
            this.Log(status.Available ? $"{player.PlayerName} asked for terrain tiles: serving them." : $"{player.PlayerName} asked for terrain tiles: not serving them: {status.Reason}");

        this.sender!.Touch(player.PlayerUID);
        this.serverChannel!.SendPacket(status, player);
    }

    private TerrainTileStatus StatusFor(IServerPlayer player)
    {
        if (this.service == null || !this.Available)
            return this.Status(false, this.UnavailableReason);

        TerrainTileOwner info = this.OwnerInfo(player.PlayerUID);
        return this.Status(info.Allowed, info.DeniedReason);
    }

    private TerrainTileStatus Status(bool available, string reason) => new()
    {
        Available = available,
        Reason = reason,
        Identity = available ? this.Identity : "",
        MaxWantedTiles = this.config.MaxWantedTiles,
        MaxDistancePerStep = this.config.MaxDistancePerStep,
        MaxDistance = this.config.MaxDistance
    };

    // The service has gone off after a run of failures, or come back: every client that said hello hears of it.
    private void FollowService(TerrainTileService current)
    {
        string? failure = current.FailureReason;
        if (failure != null && this.Available)
            this.SetUnavailable(failure);
        else if (failure == null && !this.Available)
            this.SetAvailable();
        else
            return;

        foreach (string uid in this.greeted.ToArray())
        {
            if (this.sapi!.World.PlayerByUid(uid) is IServerPlayer player && player.ConnectionState != EnumClientState.Offline)
                this.serverChannel!.SendPacket(this.StatusFor(player), player);
        }
    }

    private void OnWant(IServerPlayer player, TerrainTileWant want) => this.sender!.OnWant(player.PlayerUID, want, this.service, this.UnavailableReason);

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        _ = this.greeted.Remove(player.PlayerUID);
        this.service?.DropOwner(player.PlayerUID);
        this.sender?.Remove(player.PlayerUID);
    }

    // A player who has gone is dropped by the disconnect event, never from here: the sender may be walking its clients.
    private void SendTo<T>(string uid, T message)
    {
        if (this.sapi?.World.PlayerByUid(uid) is IServerPlayer player && player.ConnectionState != EnumClientState.Offline)
            this.serverChannel?.SendPacket(message, player);
    }

    private void OnServerTick(float dt)
    {
        long now = Environment.TickCount64;
        long interval = now - this.lastTick;
        this.lastTick = now;

        // Mods on the server hear what was answered since the last tick; their callbacks' exceptions are caught there.
        this.local!.Tick();

        if (this.process != null && now - this.lastCpuSample >= 1000)
        {
            this.process.Refresh();
            double cpu = this.process.TotalProcessorTime.TotalMilliseconds;
            this.cpuPercent = Math.Clamp((cpu - this.lastCpuMilliseconds) * 100 / ((now - this.lastCpuSample) * (double)Environment.ProcessorCount), 0, 100);
            this.lastCpuMilliseconds = cpu;
            this.lastCpuSample = now;
        }

        // The sender runs without a service too: what it refused before the service started still has to go out.
        TerrainTileService? current = this.service;
        this.serverTick.Run(() =>
        {
            if (current != null)
            {
                current.UpdateLoad(this.cpuPercent, interval);
                current.Tick();
                this.FollowService(current);
            }

            this.sender!.Tick(this.config.TransferKilobytesPerTick * 1024, now, uid => current?.WantedBy(uid) ?? 0, current?.Running ?? 0, current?.Waiting ?? 0);
        });

        if (current != null && this.config.LogTileStats && now - this.lastStatsLog >= 60000 && (current.Running > 0 || current.Waiting > 0 || this.sender!.Clients > 0))
        {
            this.lastStatsLog = now;
            this.Log($"tile stats: {this.Describe().Replace('\n', ' ')}");
        }
    }

    /// <summary><c>/terrainsampler tilefetch [step] [radius]</c>: tiles round the caller, or spawn from the console, made as a server mod would ask for them.</summary>
    internal TextCommandResult ServerFetch(TextCommandCallingArgs args)
    {
        ICoreServerAPI api = this.sapi!;
        IServerPlayer? caller = args.Caller.Player as IServerPlayer;
        EntityPos position = args.Caller.Player?.Entity?.Pos ?? api.World.DefaultSpawnPosition;

        // Mods on the server share one MaxWantedTiles between them, so the square is never wider than fits it.
        int widest = Math.Min(16, TerrainTileConfig.SquareRadiusWithin(this.config.MaxWantedTiles));
        return this.Fetch((int)args[0], (int)args[1], widest, position, Sources, Report, "; the server log says when they are done");

        Func<string> Sources()
        {
            TerrainTileService current = this.service!;
            long madeBefore = current.Generated, bytesBefore = current.GeneratedBytes, diskBefore = current.DiskHits, memoryBefore = current.MemoryHits;
            double sampleBefore = current.GenerateMilliseconds;
            return () =>
            {
                long made = current.Generated - madeBefore;
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{made} made ({(made > 0 ? (current.GenerateMilliseconds - sampleBefore) / made : 0):0} ms each to sample), {current.DiskHits - diskBefore} from disk, {current.MemoryHits - memoryBefore} from memory; {(made > 0 ? (current.GeneratedBytes - bytesBefore) / 1024.0 / made : 0):0.0} KiB a tile made");
            };
        }

        void Report(string message)
        {
            this.Log(message);
            caller?.SendMessage(GlobalConstants.GeneralChatGroup, message, EnumChatType.Notification);
        }
    }

    /// <summary>The service's state, for <c>/terrainsampler tiles</c>.</summary>
    public string Describe()
    {
        if (this.service == null)
            return $"The terrain tile service is not running: {this.UnavailableReason}";

        StringBuilder text = new();
        _ = text.Append(CultureInfo.InvariantCulture, $"Terrain tiles, identity {this.Identity[..12]}, network sampling {(this.config.NetworkSampling ? "on" : "off")}, game process at {this.cpuPercent:0}% CPU.\n");
        _ = text.Append(this.service.Describe());
        _ = text.Append(CultureInfo.InvariantCulture, $"\nSent: {this.sender!.TilesSent} tiles, {this.sender.BytesSent / (1024.0 * 1024.0):0.0} MiB, and {this.sender.TilesRefused} refusals in {this.sender.RefusalsSent} message(s), to {this.sender.Clients} client(s) seen.");
        return text.ToString();
    }
}
