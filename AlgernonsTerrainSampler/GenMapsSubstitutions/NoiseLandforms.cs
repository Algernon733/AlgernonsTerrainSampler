using System.Threading;
using Vintagestory.API.Server;
using Vintagestory.ServerMods.NoObf;

namespace AlgernonsTerrainSampler.GenMapsSubstitutions;

public static class NoiseLandforms
{
    private static readonly Lock _loadLock = new();

    public static LandformsWorldProperty LandformsProperty { get; set; }

    public static void LoadLandforms(ICoreServerAPI api) => LoadLandforms(api, reload: false);

    /// <param name="reload">
    /// Load them again even if they are loaded. A world start must: the thresholds depend on that world's height,
    /// and a second world in the same process may have a different one, or different mods patching the asset.
    /// </param>
    public static void LoadLandforms(ICoreServerAPI api, bool reload)
    {
        if (LandformsProperty != null && !reload)
            return;

        lock (_loadLock)
        {
            if (LandformsProperty != null && !reload)
                return;

            // Built whole before it is published, so nothing ever reads a half-initialised set.
            LandformsWorldProperty landforms = api.Assets.Get("worldgen/landforms.json").ToObject<LandformsWorldProperty>(null);

            int quantityMutations = 0;
            for (int i = 0; i < landforms.Variants.Length; i++)
            {
                LandformVariant variant = landforms.Variants[i];
                variant.index = i;
                variant.Init(api.WorldManager, i);
                if (variant.Mutations != null)
                    quantityMutations += variant.Mutations.Length;
            }

            landforms.LandFormsByIndex = new LandformVariant[quantityMutations + landforms.Variants.Length];
            for (int j = 0; j < landforms.Variants.Length; j++)
                landforms.LandFormsByIndex[j] = landforms.Variants[j];

            int nextIndex = landforms.Variants.Length;
            for (int k = 0; k < landforms.Variants.Length; k++)
            {
                LandformVariant variant2 = landforms.Variants[k];
                if (variant2.Mutations != null)
                {
                    for (int l = 0; l < variant2.Mutations.Length; l++)
                    {
                        LandformVariant variantMutation = variant2.Mutations[l];
                        variantMutation.TerrainOctaves ??= variant2.TerrainOctaves;
                        variantMutation.TerrainOctaveThresholds ??= variant2.TerrainOctaveThresholds;
                        variantMutation.TerrainYKeyPositions ??= variant2.TerrainYKeyPositions;
                        variantMutation.TerrainYKeyThresholds ??= variant2.TerrainYKeyThresholds;
                        landforms.LandFormsByIndex[nextIndex] = variantMutation;
                        variantMutation.Init(api.WorldManager, nextIndex);
                        nextIndex++;
                    }
                }
            }

            LandformsProperty = landforms;
        }
    }
}
