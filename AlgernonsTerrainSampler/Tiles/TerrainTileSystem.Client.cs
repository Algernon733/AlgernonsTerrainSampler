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

/// <summary>A client's half: the channel to the server, its caches, its commands.</summary>
public sealed partial class TerrainTileSystem
{
    private TerrainTileTickGuard clientTick = null!;
    private ICoreClientAPI? capi;
    private IClientNetworkChannel? clientChannel;
    private TerrainTileClientCore? client;
    private long clientTickListener;

    public override void StartClientSide(ICoreClientAPI api)
    {
        this.capi = api;
        ClientInstance = this;
        this.config = LoadConfig(api, store: false);
        this.client = new TerrainTileClientCore(
            want => this.clientChannel?.SendPacket(want),
            this.OpenClientDisk,
            this.Log,
            () => this.clientChannel?.SendPacket(new TerrainTileHello()),
            received => this.clientChannel?.SendPacket(received));
        this.client.AvailabilityChanged += this.OnClientAvailabilityChanged;
        this.client.StatusReceived += this.OnClientStatus;
        this.requests = this.client.Requests;
        this.UnavailableReason = this.client.UnavailableReason;
        this.clientTick = new TerrainTileTickGuard("the terrain tile client's tick", message => api.Logger.Error("{0}", LogPrefix + message), this.Log);

        this.clientChannel = api.Network.RegisterChannel(TerrainTileProtocol.ChannelName)
            .RegisterMessageType<TerrainTileHello>()
            .RegisterMessageType<TerrainTileStatus>()
            .RegisterMessageType<TerrainTileWant>()
            .RegisterMessageType<TerrainTileProgress>()
            .RegisterMessageType<TerrainTileData>()
            .RegisterMessageType<TerrainTileRefused>()
            .RegisterMessageType<TerrainTileReceived>()
            .SetMessageHandler<TerrainTileStatus>(this.client.OnStatus)
            .SetMessageHandler<TerrainTileProgress>(this.client.OnProgress)
            .SetMessageHandler<TerrainTileData>(this.client.OnData)
            .SetMessageHandler<TerrainTileRefused>(this.client.OnRefused);

        this.clientTickListener = api.Event.RegisterGameTickListener(this.OnClientTick, 50);
        this.RegisterClientCommands(api);
    }

    private void OnClientTick(float dt) => this.clientTick.Run(this.ClientTick);

    private void ClientTick()
    {
        this.client!.Tick(this.clientChannel!.Connected, Environment.TickCount64);
        this.MirrorClient();
    }

    // The core is the truth on a client; these properties show it, whether or not availability changed.
    private void MirrorClient()
    {
        TerrainTileClientCore core = this.client!;
        this.Available = core.Available;
        this.UnavailableReason = core.UnavailableReason;
        this.Identity = core.Identity;
    }

    private void OnClientAvailabilityChanged()
    {
        this.MirrorClient();
        this.RaiseAvailabilityChanged();
    }

    private void OnClientStatus()
    {
        TerrainTileClientCore core = this.client!;
        this.MirrorClient();
        this.capi?.Logger.Notification("{0}", LogPrefix + (core.Available
            ? $"the server serves terrain tiles, identity {core.Identity[..12]}."
            : $"the server does not serve terrain tiles: {core.UnavailableReason}"));
    }

    private TerrainTileDiskCache? OpenClientDisk(string identity)
    {
        // In single player the server's cache is on this machine already.
        ICoreClientAPI api = this.capi!;
        if (api.IsSinglePlayer || this.config.ClientDiskCacheMegabytes <= 0)
            return null;

        try
        {
            string root = api.GetOrCreateDataPath(Path.Combine("ModData", "algernonsterrainsampler", "remote"));
            return new TerrainTileDiskCache(root, identity, this.config.ClientDiskCacheMegabytes, this.Log, unusedDays: UnusedCacheDays);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.Log($"no client tile cache: {ex.Message}");
            return null;
        }
    }

    private void RegisterClientCommands(ICoreClientAPI api)
    {
        _ = api.ChatCommands.Create("terrainsampler")
            .WithDescription("Terrain Sampler, this client's side")
            .BeginSubCommand("tiles")
                .WithDescription("Show whether the server serves terrain tiles, and what this client has had from it")
                .HandleWith(_ => TextCommandResult.Success(this.DescribeClient()))
            .EndSubCommand()
            .BeginSubCommand("fetch")
                .WithDescription("Ask for the square of tiles round you, and report how long they took")
                .WithArgs(api.ChatCommands.Parsers.OptionalInt("step", 4), api.ChatCommands.Parsers.OptionalInt("radius", 1))
                .HandleWith(this.CmdFetch)
            .EndSubCommand();
    }

    // .terrainsampler fetch [step] [radius]. The client holds what the server's books have no room for, so the square
    // need not fit them; eight is wide enough to see the network work.
    private TextCommandResult CmdFetch(TextCommandCallingArgs args)
    {
        ICoreClientAPI api = this.capi!;
        TerrainTileClientCore core = this.client!;
        return this.Fetch((int)args[0], (int)args[1], 8, api.World.Player.Entity.Pos, Sources, api.ShowChatMessage, "");

        Func<string> Sources()
        {
            long diskBefore = core.FromDisk, memoryBefore = core.FromMemory, receivedBefore = core.Received, bytesBefore = core.ReceivedBytes;
            return () => string.Create(
                CultureInfo.InvariantCulture,
                $"{core.Received - receivedBefore} from the server, {(core.ReceivedBytes - bytesBefore) / 1024.0:0} KiB; {core.FromDisk - diskBefore} from disk; {core.FromMemory - memoryBefore} from memory");
        }
    }

    private string DescribeClient()
    {
        TerrainTileClientCore core = this.client!;
        if (!core.Available)
            return $"Terrain tiles unavailable: {this.UnavailableReason}";

        StringBuilder text = new();
        _ = text.Append(CultureInfo.InvariantCulture, $"The server serves terrain tiles, identity {core.Identity[..12]}; up to {core.Status?.MaxWantedTiles} at once, within {core.Status?.MaxDistancePerStep} blocks per step and {core.Status?.MaxDistance} in all.\n");
        _ = text.Append(CultureInfo.InvariantCulture, $"Received {core.Received} tiles, {core.ReceivedBytes / (1024.0 * 1024.0):0.0} MiB; from disk {core.FromDisk}, from memory {core.FromMemory}, refused {core.Refused}; {core.InFlight} in flight for {core.Requests.Pending} request(s), {core.OnServer} asked of the server and {core.Held} waiting for room.");
        if (core.Disk != null)
            _ = text.Append(CultureInfo.InvariantCulture, $"\nDisk: {core.Disk.Count} tiles, {core.Disk.Bytes / (1024.0 * 1024.0):0.0} MiB.");
        else if (this.capi!.IsSinglePlayer)
            _ = text.Append("\nDisk: the server's cache, on this machine.");

        if (core.LastProgress is TerrainTileProgress progress)
            _ = text.Append(CultureInfo.InvariantCulture, $"\nServer, last report: {progress.Wanted} wanted, {progress.Sending} on their way, {progress.Making} being made, {progress.Waiting} waiting.");

        return text.ToString();
    }
}
