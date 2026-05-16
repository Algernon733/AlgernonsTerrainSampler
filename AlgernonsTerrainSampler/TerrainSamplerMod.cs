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

    private ICoreServerAPI serverAPI;
    private bool disposed;
    private MethodInfo watershedsGetHeightMethod;
    private object watershedsGenTerraInstance;

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
    /// If Watersheds is loaded, its sampled terrain height is used while climate and density maps are sampled from this mod.
    /// </summary>
    public TerrainColumnSample SampleColumn(int worldX, int worldZ)
        => this.SampleColumn(new WorldMapCoordinate(worldX, worldZ));

    /// <inheritdoc cref="SampleColumn(int, int)"/>
    public TerrainColumnSample SampleColumn(WorldMapCoordinate worldCoordinate)
    {
        if (this.GenTerra == null)
            return new TerrainColumnSample { Height = TerraGenConfig.seaLevel };

        int? heightOverride = null;
        if (this.WatershedsLoaded && this.watershedsGetHeightMethod != null && this.watershedsGenTerraInstance != null)
        {
            try
            {
                Type watershedsCoordinateType = this.watershedsGetHeightMethod.GetParameters()[0].ParameterType;
                object watershedsCoordinate = Activator.CreateInstance(watershedsCoordinateType, worldCoordinate.X, worldCoordinate.Z);
                object result = this.watershedsGetHeightMethod.Invoke(this.watershedsGenTerraInstance, [watershedsCoordinate]);
                heightOverride = (int)result;
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
            return this.GenTerra.SampleColumn(worldCoordinate, heightOverride);
        }
        catch (Exception)
        {
            return new TerrainColumnSample { Height = heightOverride ?? TerraGenConfig.seaLevel };
        }
    }

    private void TryReflectWatersheds()
    {
        const string typeName = "Watersheds.WorldGen.Terrain.WatershedsGenTerra";
        const string methodName = "GetPreWatershedsBlockColumnHeight";
        string failure;

        try
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
            this.watershedsGetHeightMethod = type?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            this.watershedsGenTerraInstance = type == null ? null : this.serverAPI.ModLoader.Systems.FirstOrDefault(s => s.GetType() == type);

            if (type == null)
            {
                failure = $"type '{typeName}' not found";
            }
            else if (this.watershedsGetHeightMethod == null)
            {
                failure = $"method '{methodName}' missing";
            }
            else if (this.watershedsGenTerraInstance == null)
            {
                failure = "no watershedsGenTerraInstance";
            }
            else
            {
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
        this.watershedsGenTerraInstance = null;
        this.WatershedsLoaded = false;
        Mapping.ThreadSafeRegionCache.Dispose();
        Terrain.TerrainGenerationLib.DisposeContextCache();
        base.Dispose();
    }
}
