namespace AlgernonsTerrainSampler;

/// <summary>
/// Sampled terrain and worldgen-map data for one world block column.
/// </summary>
public readonly struct TerrainColumnSample
{
    /// <summary>
    /// Predicted terrain surface height for this column.
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// Raw Vintage Story climate map color. Temperature is stored in red, rainfall in green.
    /// </summary>
    public int ClimateColor { get; init; }

    /// <summary>
    /// Height-adjusted rainfall as a 0..1 fraction.
    /// </summary>
    public float Rainfall { get; init; }

    /// <summary>
    /// Raw climate-map temperature as a 0..1 fraction.
    /// Consumers can apply Vintage Story's height/season adjustments as needed.
    /// </summary>
    public float Temperature { get; init; }

    /// <summary>
    /// Forest map density as a 0..1 fraction. This is a density-map sample, not final tree placement.
    /// </summary>
    public float ForestDensity { get; init; }

    /// <summary>
    /// Shrub map density as a 0..1 fraction. This is a density-map sample, not final shrub placement.
    /// </summary>
    public float ShrubDensity { get; init; }

    /// <summary>
    /// The block code of the rock at the top of the column, under its soil, sand or gravel: what vanilla's rock strata put
    /// there (for example <c>game:rock-sandstone</c>), which is also the rock worldgen's block layers take their type from.
    /// Empty when it is not known: no strata generator the sampler knows, or a sample that failed. Only
    /// <c>SampleColumnWithTopRock</c> and the tiles fill it; <c>SampleColumn</c> leaves it empty, as before 2.0.0.
    /// </summary>
    public string TopRock
    {
        get => this.topRock ?? "";
        init => this.topRock = value;
    }

    private readonly string topRock;
}
