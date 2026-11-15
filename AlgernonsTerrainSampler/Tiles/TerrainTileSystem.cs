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

/// <summary>
/// Terrain tiles for other mods, on either side. On the server, tiles come from the sampler through a scheduler, a memory
/// cache and a disk cache, and go out to clients over this mod's channel. On a client, they come from the server, through
/// the client's own caches. In single player both halves run, and the client uses the server's cache.
/// </summary>
/// <remarks>
/// <para>Ask <see cref="ServerInstance"/> on the server, <see cref="ClientInstance"/> on a client. Wait for
/// <see cref="Available"/>, or for <see cref="AvailabilityChanged"/>, then <see cref="RequestTile"/>; every call and every
/// callback is on the main thread. Tiles are read only and shared between consumers.</para>
/// <para>The signatures use only .NET's own types and <see cref="TerrainTile"/>, so a mod can call this by reflection with
/// no compile-time reference: an <c>Action&lt;object&gt;</c> may be passed for <c>Action&lt;TerrainTile&gt;</c>.</para>
/// </remarks>
public sealed partial class TerrainTileSystem : ModSystem
{
    private const string LogPrefix = "AlgernonsTerrainSampler: ";

    private TerrainTileConfig config = new();
    private TerrainTileRequests? requests;

    /// <summary>The server's tile service, once the server has started; null on a client and before then.</summary>
    public static TerrainTileSystem? ServerInstance { get; private set; }

    /// <summary>The client's tile service; null on a server.</summary>
    public static TerrainTileSystem? ClientInstance { get; private set; }

    /// <summary>Cells across every tile. A tile at step s spans <c>TileCells * s</c> blocks.</summary>
    public static int TileCells => TerrainTileKey.Cells;

    /// <summary>Samples across every tile: <see cref="TileCells"/> + 1, the last shared with the next tile.</summary>
    public static int TileSamples => TerrainTileKey.Samples;

    /// <summary>Whether tiles can be asked for. On a client: whether the server serves them to this player.</summary>
    public bool Available { get; private set; }

    /// <summary>Why not, when not <see cref="Available"/>.</summary>
    public string UnavailableReason { get; private set; } = "The terrain tile service has not started.";

    /// <summary>The cache identity: the same identity means the same tiles. Empty when not available.</summary>
    public string Identity { get; private set; } = "";

    /// <summary>Raised on the main thread when <see cref="Available"/> changes.</summary>
    public event Action? AvailabilityChanged;

    public override bool ShouldLoad(EnumAppSide side) => true;

    public override double ExecuteOrder() => 0.02;

    /// <summary>The index of the tile at <paramref name="step"/> holding world coordinate <paramref name="world"/>, on either axis.</summary>
    public static int TileIndexOf(int step, int world) => (int)Math.Floor(world / (double)(TerrainTileKey.Cells * step));

    /// <summary>
    /// Asks for the tile at <paramref name="step"/> (a power of two from 1 to 64) whose corner is
    /// (<c>tileX * TileCells * step</c>, <c>tileZ * TileCells * step</c>). Lower priorities come first. Exactly one of the
    /// callbacks runs, on the main thread, never before this returns, unless the request is cancelled first. Asked before
    /// the service is available, it is refused, with why, in a later tick.
    /// </summary>
    /// <returns>A handle for <see cref="CancelTile"/>.</returns>
    /// <exception cref="InvalidOperationException">On an instance that is neither <see cref="ServerInstance"/> nor <see cref="ClientInstance"/>.</exception>
    public long RequestTile(int step, int tileX, int tileZ, int priority, Action<TerrainTile> ready, Action<string> refused)
    {
        ArgumentNullException.ThrowIfNull(ready);
        TerrainTileRequests requests = this.requests
            ?? throw new InvalidOperationException("This terrain tile system has not started on either side: ask TerrainTileSystem.ServerInstance or ClientInstance.");

        return requests.Request(new TerrainTileKey(step, tileX, tileZ), priority, ready, refused);
    }

    /// <summary>No longer wants the tile; neither callback will run. Unknown or finished handles are ignored.</summary>
    public void CancelTile(long handle) => this.requests?.Cancel(handle);

    /// <summary>
    /// The fetch commands on both sides: the square of tiles at <paramref name="step"/> round a position, asked for as any
    /// consumer would, most central first, and one line to <paramref name="report"/> when every one has been answered.
    /// <paramref name="sources"/> is called before the first request, and what it returns when the last is answered, to
    /// say where the tiles came from.
    /// </summary>
    private TextCommandResult Fetch(int step, int radius, int widest, EntityPos position, Func<Func<string>> sources, Action<string> report, string whereToLook)
    {
        if (!TerrainTileKey.IsValidStep(step))
            return TextCommandResult.Error("The step is a power of two from 1 to 64.");

        if (!this.Available)
            return TextCommandResult.Error($"Terrain tiles are unavailable: {this.UnavailableReason}");

        int asked = radius;
        radius = Math.Clamp(radius, 0, widest);
        TerrainTileKey centre = TerrainTileKey.Containing(step, (int)position.X, (int)position.Z);
        Func<string> since = sources();
        int total = ((2 * radius) + 1) * ((2 * radius) + 1);
        int ready = 0, refused = 0;
        string firstRefusal = "", centreHeights = "";
        Stopwatch timer = Stopwatch.StartNew();

        void Answered()
        {
            if (ready + refused == total)
            {
                report(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Terrain tiles at step {step} round {centre.TileX}, {centre.TileZ}: {ready} of {total} in {timer.ElapsedMilliseconds} ms; {since()}{(refused > 0 ? $"; {refused} refused: {firstRefusal}" : "")}.{centreHeights}"));
            }
        }

        for (int dz = -radius; dz <= radius; dz++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                bool isCentre = dx == 0 && dz == 0;
                _ = this.RequestTile(step, centre.TileX + dx, centre.TileZ + dz, (dx * dx) + (dz * dz),
                    tile =>
                    {
                        ready++;
                        if (isCentre)
                            centreHeights = string.Create(CultureInfo.InvariantCulture, $" The centre tile's heights run {tile.Height.Min()} to {tile.Height.Max()}.");
                        Answered();
                    },
                    reason =>
                    {
                        refused++;
                        if (firstRefusal.Length == 0)
                            firstRefusal = reason;
                        Answered();
                    });
            }
        }

        string narrowed = radius < asked ? string.Create(CultureInfo.InvariantCulture, $", radius {radius}, the widest here") : "";
        return TextCommandResult.Success(string.Create(CultureInfo.InvariantCulture, $"Asked for {total} tile(s) at step {step} round {centre.TileX}, {centre.TileZ}{narrowed}{whereToLook}."));
    }

    private void SetAvailable()
    {
        this.Available = true;
        this.UnavailableReason = "";
        this.Identity = this.serviceIdentity;
        this.RaiseAvailabilityChanged();
    }

    private void SetUnavailable(string reason)
    {
        bool changed = this.Available;
        this.Available = false;
        this.UnavailableReason = reason;
        this.Identity = "";
        if (changed)
            this.RaiseAvailabilityChanged();
    }

    private void RaiseAvailabilityChanged()
    {
        try
        {
            this.AvailabilityChanged?.Invoke();
        }
        catch (Exception ex)
        {
            this.Log($"a consumer's availability handler threw: {ex}");
        }
    }

    private void Log(string message) => ((ICoreAPI?)this.sapi ?? this.capi)?.Logger.Notification("{0}", LogPrefix + message);

    private static TerrainTileConfig LoadConfig(ICoreAPI api, bool store)
    {
        TerrainTileConfig config;
        try
        {
            config = api.LoadModConfig<TerrainTileConfig>(TerrainTileConfig.FileName) ?? new TerrainTileConfig();
        }
        catch (Exception ex)
        {
            api.Logger.Warning("{0}", LogPrefix + $"{TerrainTileConfig.FileName} could not be read, so the defaults are used: {ex.Message}");
            config = new TerrainTileConfig();
            store = false; // Leave the broken file for its owner to fix.
        }

        config.Clamp();
        if (store)
            api.StoreModConfig(config, TerrainTileConfig.FileName);

        return config;
    }

    /// <summary>A cache folder, a world's or a server's, whose tiles nobody has read or written for this long is deleted at start.</summary>
    private const int UnusedCacheDays = 90;

    private static string SafeFolderName(string? savegame)
    {
        if (!string.IsNullOrEmpty(savegame) && savegame.Length <= 64 && savegame.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return savegame;

        return TerrainTileIdentity.Hash(writer => writer.Write(savegame ?? ""))[..32];
    }

    public override void Dispose()
    {
        if (this.sapi != null)
        {
            this.sapi.Event.UnregisterGameTickListener(this.serverTickListener);
            this.sapi.Event.PlayerDisconnect -= this.OnPlayerDisconnect;
            this.StopService();
            if (ServerInstance == this)
                ServerInstance = null;
        }

        if (this.capi != null)
        {
            this.capi.Event.UnregisterGameTickListener(this.clientTickListener);
            this.client?.Dispose();
            if (ClientInstance == this)
                ClientInstance = null;
        }

        this.AvailabilityChanged = null;
        base.Dispose();
    }
}

/// <summary>
/// Runs a tick that may throw. The first failure of a run is logged in full and the rest only counted, every tick is
/// tried again, and the first that works says how many failed: a broken tick neither floods the log, twenty lines a
/// second, nor stalls its consumers without a word.
/// </summary>
internal sealed class TerrainTileTickGuard
{
    private readonly string what;
    private readonly Action<string> logError;
    private readonly Action<string> log;
    private int failed;

    public TerrainTileTickGuard(string what, Action<string> logError, Action<string> log)
    {
        this.what = what;
        this.logError = logError;
        this.log = log;
    }

    /// <summary>Ticks in a row that have failed; 0 once one works.</summary>
    public int Failing => this.failed;

    public void Run(Action tick)
    {
        try
        {
            tick();
        }
        catch (Exception ex)
        {
            if (this.failed++ == 0)
                this.logError($"{this.what} failed, and is tried again every tick; until it works, further failures are only counted: {ex}");

            return;
        }

        if (this.failed > 0)
        {
            this.log(string.Create(CultureInfo.InvariantCulture, $"{this.what} works again, after {this.failed} failed tick(s)."));
            this.failed = 0;
        }
    }
}
