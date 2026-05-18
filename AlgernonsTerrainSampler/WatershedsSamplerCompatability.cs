using System;
using System.Reflection;
using Vintagestory.ServerMods;

namespace AlgernonsTerrainSampler;

public sealed class WatershedsSamplerCompatability
{
    private readonly PropertyInfo height;
    private readonly PropertyInfo climateColor;
    private readonly PropertyInfo rainfall;
    private readonly PropertyInfo temperature;
    private readonly PropertyInfo forestDensity;
    private readonly PropertyInfo shrubDensity;

    private WatershedsSamplerCompatability(
        PropertyInfo height,
        PropertyInfo climateColor,
        PropertyInfo rainfall,
        PropertyInfo temperature,
        PropertyInfo forestDensity,
        PropertyInfo shrubDensity)
    {
        this.height = height;
        this.climateColor = climateColor;
        this.rainfall = rainfall;
        this.temperature = temperature;
        this.forestDensity = forestDensity;
        this.shrubDensity = shrubDensity;
    }

    public static WatershedsSamplerCompatability Setup(Type sampleType)
    {
        if (sampleType == null)
            return null;

        PropertyInfo height = sampleType.GetProperty("Height", BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo climateColor = sampleType.GetProperty("ClimateColor", BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo rainfall = sampleType.GetProperty("Rainfall", BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo temperature = sampleType.GetProperty("Temperature", BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo forestDensity = sampleType.GetProperty("ForestDensity", BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo shrubDensity = sampleType.GetProperty("ShrubDensity", BindingFlags.Public | BindingFlags.Instance);

        if (height == null || climateColor == null || rainfall == null || temperature == null || forestDensity == null || shrubDensity == null)
            return null;

        return new WatershedsSamplerCompatability(height, climateColor, rainfall, temperature, forestDensity, shrubDensity);
    }

    public TerrainColumnSample ReadSample(object watershedsSample)
    {
        if (watershedsSample == null)
            return new TerrainColumnSample { Height = TerraGenConfig.seaLevel };

        return new TerrainColumnSample
        {
            Height = (int)this.height.GetValue(watershedsSample),
            ClimateColor = (int)this.climateColor.GetValue(watershedsSample),
            Rainfall = (float)this.rainfall.GetValue(watershedsSample),
            Temperature = (float)this.temperature.GetValue(watershedsSample),
            ForestDensity = (float)this.forestDensity.GetValue(watershedsSample),
            ShrubDensity = (float)this.shrubDensity.GetValue(watershedsSample),
        };
    }
}
