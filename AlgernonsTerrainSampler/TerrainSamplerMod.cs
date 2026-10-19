using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace AlgernonsTerrainSampler;

public class TerrainSamplerMod : ModSystem
{
    public static TerrainSamplerMod Instance { get; private set; }

    public TerrainSamplerGenTerra GenTerra { get; private set; }

    public bool WatershedsLoaded { get; private set; }

    /// <summary>Whether Watersheds' detailed column sampler was found and is still in use.</summary>
    public bool WatershedsDetailedSampling => this.WatershedsLoaded && this.watershedsSampleColumnMethod != null && this.watershedsSampler != null;

    // What the sampler delegated to when the world started. The strict path refuses to answer once either has turned
    // itself off after an error, so a tile cache never mixes Watersheds' heights with the sampler's own.
    private bool watershedsAtStart;
    private bool watershedsDetailedAtStart;

    private ICoreServerAPI serverAPI;
    private bool disposed;
    private MethodInfo watershedsGetHeightMethod;
    private MethodInfo watershedsSampleColumnMethod;
    private object watershedsGenTerraInstance;
    private WatershedsSamplerCompatability watershedsSampler;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override double ExecuteOrder() => 0.01;

    public override void StartServerSide(ICoreServerAPI api)
    {
        Instance = this;
        this.serverAPI = api;

        api.Event.ServerRunPhase(EnumServerRunPhase.WorldReady, this.OnWorldReady);
    }

    private void OnWorldReady()
    {
        this.GenTerra = this.serverAPI.ModLoader.GetModSystem<TerrainSamplerGenTerra>();

        GenMaps basegameGenMaps = this.serverAPI.ModLoader.GetModSystem<GenMaps>();
        if (this.GenTerra != null && basegameGenMaps != null)
            this.GenTerra.BasegameGenMaps = basegameGenMaps;

        if (this.serverAPI.ModLoader.Mods.Any(m => m.Info?.ModID == "watersheds"))
            this.TryReflectWatersheds();

        this.watershedsAtStart = this.WatershedsLoaded;
        this.watershedsDetailedAtStart = this.WatershedsDetailedSampling;

        TerrainSamplerCommand command = new(this);
        _ = this.serverAPI.ChatCommands
            .Create("terrainsampler")
            .WithDescription("Terrain Sampler commands")
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("columnheight")
                .WithDescription("Get the terrain height at your current position")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(command.CmdColumnHeight)
            .EndSubCommand()
            .BeginSubCommand("samplecolumn")
                .WithDescription("Get a detailed terrain column sample at your current position")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(command.CmdSampleColumn)
            .EndSubCommand()
#if DEBUG
            .BeginSubCommand("columninfo")
                .WithDescription("Get debug information about the terrain generation that went into generating the block column at your current position")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(command.CmdBlockColumnTerrainHeightInfo)
            .EndSubCommand()
#endif
            ;
    }

    /// <summary>
    /// Gets the terrain height at the specified world coordinate.
    /// If Watersheds is loaded, delegates to it's sampling (which includes coastal effects etc)
    /// Otherwise uses our own terrain sampler.
    /// </summary>
    public int GetBlockColumnHeight(int worldX, int worldZ)
        => this.GetBlockColumnHeight(new WorldMapCoordinate(worldX, worldZ));

    /// <inheritdoc cref="GetBlockColumnHeight(int, int)"/>
    public int GetBlockColumnHeight(WorldMapCoordinate worldCoordinate)
    {
        if (this.GenTerra == null)
            return TerraGenConfig.seaLevel;

        if (this.WatershedsLoaded && this.watershedsGetHeightMethod != null && this.watershedsGenTerraInstance != null)
        {
            try
            {
                Type watershedsCoordinateType = this.watershedsGetHeightMethod.GetParameters()[0].ParameterType;
                object watershedsCoordinate = Activator.CreateInstance(watershedsCoordinateType, worldCoordinate.X, worldCoordinate.Z);
                object result = this.watershedsGetHeightMethod.Invoke(this.watershedsGenTerraInstance, [watershedsCoordinate]);
                return (int)result;
            }
            catch (Exception ex)
            {
                // Disable to avoid log spam
                this.WatershedsLoaded = false;

                this.serverAPI.Logger.Warning(
                    "AlgernonsTerrainSampler: Watersheds height sample failed. Falling back to use the normal sampler. " +
                    "Your terrain samples may be inconsistent with the actual terrain. {0}",
                    ex);
            }
        }

        try
        {
            return this.GenTerra.GetBlockColumnHeight(worldCoordinate);
        }
        catch (Exception)
        {
            return TerraGenConfig.seaLevel;
        }
    }

    /// <summary>
    /// Samples terrain height and worldgen map data at the specified world coordinate.
    /// If Watersheds is loaded, we delegate to watersheds' sampler.
    /// </summary>
    public TerrainColumnSample SampleColumn(int worldX, int worldZ)
        => this.SampleColumn(new WorldMapCoordinate(worldX, worldZ));

    /// <inheritdoc cref="SampleColumn(int, int)"/>
    public TerrainColumnSample SampleColumn(WorldMapCoordinate worldCoordinate)
    {
        if (this.GenTerra == null)
            return new TerrainColumnSample { Height = TerraGenConfig.seaLevel };

        if (this.TrySampleWatershedsColumn(worldCoordinate, rethrow: false, out TerrainColumnSample watershedsSample))
            return watershedsSample;

        int? heightOverride = this.SampleWatershedsHeight(worldCoordinate, rethrow: false);

        try
        {
            return this.GenTerra.SampleColumn(worldCoordinate, heightOverride);
        }
        catch (Exception)
        {
            return new TerrainColumnSample { Height = heightOverride ?? TerraGenConfig.seaLevel };
        }
    }

    /// <summary>
    /// <see cref="SampleColumn(WorldMapCoordinate)"/> without its fallbacks: a failure throws rather than answering
    /// sea level, and so does a Watersheds delegation that has turned itself off since the world started. Anything
    /// that keeps samples, such as the tile cache, must use this, or one bad moment becomes flat ground for good.
    /// </summary>
    internal TerrainColumnSample SampleColumnOrThrow(WorldMapCoordinate worldCoordinate)
    {
        TerrainSamplerGenTerra genTerra = this.GenTerra ?? throw new InvalidOperationException("The terrain sampler has not started.");
        if (this.WatershedsLoaded != this.watershedsAtStart || this.WatershedsDetailedSampling != this.watershedsDetailedAtStart)
            throw new InvalidOperationException("Watersheds delegation changed after an error; restart the server to sample again.");

        if (this.TrySampleWatershedsColumn(worldCoordinate, rethrow: true, out TerrainColumnSample watershedsSample))
            return watershedsSample;

        return genTerra.SampleColumn(worldCoordinate, this.SampleWatershedsHeight(worldCoordinate, rethrow: true));
    }

    private bool TrySampleWatershedsColumn(WorldMapCoordinate worldCoordinate, bool rethrow, out TerrainColumnSample sample)
    {
        sample = default;
        if (!this.WatershedsLoaded || this.watershedsSampleColumnMethod == null
            || this.watershedsGenTerraInstance == null || this.watershedsSampler == null)
            return false;

        try
        {
            Type watershedsCoordinateType = this.watershedsSampleColumnMethod.GetParameters()[0].ParameterType;
            object watershedsCoordinate = Activator.CreateInstance(watershedsCoordinateType, worldCoordinate.X, worldCoordinate.Z);
            object sampleResult = this.watershedsSampleColumnMethod.Invoke(this.watershedsGenTerraInstance, [watershedsCoordinate]);
            sample = this.watershedsSampler.ReadSample(sampleResult);
            return true;
        }
        catch (Exception ex) when (!rethrow)
        {
            this.watershedsSampleColumnMethod = null;
            this.watershedsSampler = null;

            this.serverAPI.Logger.Warning(
                "AlgernonsTerrainSampler: Watersheds' detailed sampler threw an exception. Falling back to just the height sampler. " +
                "You wont get climate, rainfall, forest opacity etc. {0}",
                ex);
            return false;
        }
    }

    private int? SampleWatershedsHeight(WorldMapCoordinate worldCoordinate, bool rethrow)
    {
        if (!this.WatershedsLoaded || this.watershedsGetHeightMethod == null || this.watershedsGenTerraInstance == null)
            return null;

        try
        {
            Type watershedsCoordinateType = this.watershedsGetHeightMethod.GetParameters()[0].ParameterType;
            object watershedsCoordinate = Activator.CreateInstance(watershedsCoordinateType, worldCoordinate.X, worldCoordinate.Z);
            object result = this.watershedsGetHeightMethod.Invoke(this.watershedsGenTerraInstance, [watershedsCoordinate]);
            return (int)result;
        }
        catch (Exception ex) when (!rethrow)
        {
            // Disable to avoid log spam
            this.WatershedsLoaded = false;

            this.serverAPI.Logger.Warning(
                "AlgernonsTerrainSampler: Watershed's height sampler threw an exception. Falling back to use the normal sampler. " +
                "Your terrain samples may be inconsistent with the actual terrain. {0}",
                ex);
            return null;
        }
    }

    private void TryReflectWatersheds()
    {
        const string typeName = "Watersheds.WorldGen.Terrain.WatershedsGenTerra";
        const string heightMethodName = "GetPreWatershedsBlockColumnHeight";
        const string sampleMethodName = "SamplePreWatershedsColumn";
        string failure;

        try
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
            this.watershedsGetHeightMethod = type?.GetMethod(heightMethodName, BindingFlags.Public | BindingFlags.Instance);
            this.watershedsSampleColumnMethod = type?.GetMethod(sampleMethodName, BindingFlags.Public | BindingFlags.Instance);
            this.watershedsGenTerraInstance = type == null ? null : this.serverAPI.ModLoader.Systems.FirstOrDefault(s => s.GetType() == type);

            if (type == null)
            {
                failure = $"type '{typeName}' not found";
            }
            else if (this.watershedsGetHeightMethod == null)
            {
                failure = $"method '{heightMethodName}' missing";
            }
            else if (this.watershedsGenTerraInstance == null)
            {
                failure = "no watershedsGenTerraInstance";
            }
            else
            {
                if (this.watershedsSampleColumnMethod != null)
                    this.watershedsSampler = WatershedsSamplerCompatability.Setup(this.watershedsSampleColumnMethod.ReturnType);

                this.WatershedsLoaded = true;
                return;
            }
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
        }

        this.serverAPI.Logger.Warning("AlgernonsTerrainSampler: Watersheds delegation disabled: {0}", failure);
    }

    public override void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        Instance = null;
        this.serverAPI = null;
        this.GenTerra = null;
        this.watershedsGetHeightMethod = null;
        this.watershedsSampleColumnMethod = null;
        this.watershedsGenTerraInstance = null;
        this.watershedsSampler = null;
        this.WatershedsLoaded = false;
        Mapping.ThreadSafeRegionCache.Dispose();
        Terrain.TerrainGenerationLib.DisposeContextCache();
        base.Dispose();
    }
}
