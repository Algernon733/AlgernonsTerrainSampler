#nullable enable
using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace AlgernonsTerrainSampler.GenMapsSubstitutions;

/// <summary>
/// Vanilla's landform map layer (<c>MapLayerLandforms</c> with its <c>NoiseLandforms</c>), line for line, over the
/// sampler's own landforms (<see cref="NoiseLandforms.LandformsProperty"/>) and with weights of its own.
/// </summary>
/// <remarks>
/// Vanilla's picks a landform by writing each variant's weight into <c>LandformVariant.WeightTmp</c> and reading it back,
/// on the game's static landforms, which every instance of the layer shares. A copy of vanilla's class, under the
/// sampler's own lock, would still race the game's worldgen thread there, and could change the landform map the game
/// saves for a region. Constructing vanilla's also reloads the game's static landforms. This one does neither.
/// </remarks>
internal sealed class SamplerLandformLayer : MapLayerBase
{
    private readonly Picker picker;
    private readonly NoiseClimate climateNoise;
    private readonly NormalizedSimplexNoise noisegenX;
    private readonly NormalizedSimplexNoise noisegenY;
    private readonly float wobbleIntensity;

    /// <summary>As <c>new MapLayerLandforms(seed, climateNoise, api, landformScale)</c>.</summary>
    public SamplerLandformLayer(long seed, NoiseClimate climateNoise, ICoreServerAPI api, float landformScale, LandformsWorldProperty landforms)
        : base(seed)
    {
        this.climateNoise = climateNoise;
        float scale = TerraGenConfig.landformMapScale * landformScale;
        scale *= Math.Max(1f, api.WorldManager.MapSizeY / 256f);
        this.picker = new Picker(seed, landforms, scale);
        int octaves = 2;
        float noiseScale = 2f * TerraGenConfig.landformMapScale;
        float persistence = 0.9f;
        this.wobbleIntensity = TerraGenConfig.landformMapScale * 1.5f;
        this.noisegenX = NormalizedSimplexNoise.FromDefaultOctaves(octaves, (double)(1f / noiseScale), (double)persistence, seed + 2);
        this.noisegenY = NormalizedSimplexNoise.FromDefaultOctaves(octaves, (double)(1f / noiseScale), (double)persistence, seed + 1231296);
    }

    public override int[] GenLayer(int xCoord, int zCoord, int sizeX, int sizeZ)
    {
        int[] result = new int[sizeX * sizeZ];
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                int offsetX = (int)((double)this.wobbleIntensity * this.noisegenX.Noise(xCoord + x, zCoord + z) * (double)1.2f);
                int offsetZ = (int)((double)this.wobbleIntensity * this.noisegenY.Noise(xCoord + x, zCoord + z) * (double)1.2f);
                int finalX = xCoord + x + offsetX;
                int finalZ = zCoord + z + offsetZ;
                int climate = this.climateNoise.GetLerpedClimateAt(finalX / TerraGenConfig.climateMapScale, finalZ / TerraGenConfig.climateMapScale);
                int rain = (climate >> 8) & 0xFF;
                int temperature = Climate.GetScaledAdjustedTemperature((climate >> 16) & 0xFF, 0);
                result[(z * sizeX) + x] = this.picker.GetLandformIndexAt(finalX, finalZ, temperature, rain);
            }
        }

        return result;
    }

    /// <summary>Vanilla's <c>NoiseLandforms</c>' picking, with its weights in an array of its own.</summary>
    private sealed class Picker : NoiseBase
    {
        private readonly LandformsWorldProperty landforms;
        private readonly float scale;
        private readonly double[] weights;

        public Picker(long seed, LandformsWorldProperty landforms, float scale)
            : base(seed)
        {
            this.landforms = landforms;
            this.scale = scale;
            this.weights = new double[landforms.Variants.Length];
        }

        public int GetLandformIndexAt(int unscaledXpos, int unscaledZpos, int temp, int rain)
        {
            float x = unscaledXpos / this.scale;
            float z = unscaledZpos / this.scale;
            int parent = this.GetParentLandformIndexAt((int)x, (int)z, temp, rain);
            LandformVariant[] mutations = this.landforms.Variants[parent].Mutations;
            if (mutations != null && mutations.Length != 0)
            {
                this.InitPositionSeed(unscaledXpos / 2, unscaledZpos / 2);
                float chance = this.NextInt(101) / 100f;
                for (int i = 0; i < mutations.Length; i++)
                {
                    LandformVariant mutation = mutations[i];
                    if (mutation.UseClimateMap)
                    {
                        int rainOutside = rain - GameMath.Clamp(rain, mutation.MinRain, mutation.MaxRain);
                        double temperatureOutside = (float)temp - GameMath.Clamp((float)temp, mutation.MinTemp, mutation.MaxTemp);
                        if (rainOutside != 0 || temperatureOutside != 0.0)
                            continue;
                    }

                    chance -= mutations[i].Chance;
                    if (chance <= 0f)
                        return mutations[i].index;
                }
            }

            return parent;
        }

        private int GetParentLandformIndexAt(int xpos, int zpos, int temp, int rain)
        {
            this.InitPositionSeed(xpos, zpos);
            LandformVariant[] variants = this.landforms.Variants;
            double total = 0.0;
            int i;
            for (i = 0; i < variants.Length; i++)
            {
                double weight = variants[i].Weight;
                if (variants[i].UseClimateMap)
                {
                    int rainOutside = rain - GameMath.Clamp(rain, variants[i].MinRain, variants[i].MaxRain);
                    double temperatureOutside = (float)temp - GameMath.Clamp((float)temp, variants[i].MinTemp, variants[i].MaxTemp);
                    if (rainOutside != 0 || temperatureOutside != 0.0)
                        weight = 0.0;
                }

                this.weights[i] = weight;
                total += weight;
            }

            double pick = total * this.NextInt(10000) / 10000.0;
            for (i = 0; i < variants.Length; i++)
            {
                pick -= this.weights[i];
                if (pick <= 0.0)
                    return variants[i].index;
            }

            return variants[i].index;
        }
    }
}
