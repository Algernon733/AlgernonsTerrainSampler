#nullable enable
using System.Reflection;
using Vintagestory.API.Server;

namespace AlgernonsTerrainSampler;

/// <summary>
/// The seed the game generates terrain from.
/// </summary>
/// <remarks>
/// From game 1.23 that is <c>IWorldManagerAPI.SeedWorldgen</c>, which only the server has; the world's <c>Seed</c> is
/// then a separate random number that no longer shapes the terrain (a world from an earlier game takes its old seed into
/// <c>SeedWorldgen</c> when 1.23 first loads it, and is given a new <c>Seed</c>). Earlier games have only <c>Seed</c>,
/// and generate from it. Read by reflection, so that one build samples the right terrain on both.
/// </remarks>
internal static class WorldgenSeed
{
    private static readonly PropertyInfo? SeedWorldgen =
        typeof(IWorldManagerAPI).GetProperty("SeedWorldgen", BindingFlags.Public | BindingFlags.Instance);

    /// <summary>The seed <paramref name="world"/>'s terrain, maps and rock strata are generated from.</summary>
    public static int Of(IWorldManagerAPI world)
        => SeedWorldgen?.GetValue(world) is int seed ? seed : world.Seed;
}
