namespace AlgernonsTerrainSampler.Tests;

/// <summary>
/// The sampler's per-region caches are bounded: sampling far and wide (the tile service does it for its clients) must
/// not keep every map region it ever touched.
/// </summary>
public class RegionCacheTests
{
    [Fact]
    public void A_region_is_made_once_and_then_served()
    {
        using Mapping.RegionCache<object> cache = new(16);
        int made = 0;
        object first = cache.GetOrAdd(3, -4, () => { made++; return new object(); });
        object again = cache.GetOrAdd(3, -4, () => { made++; return new object(); });
        Assert.Same(first, again);
        Assert.Equal(1, made);
        Assert.NotSame(first, cache.GetOrAdd(-4, 3, () => new object()));
    }

    [Fact]
    public void It_never_keeps_more_regions_than_it_was_given()
    {
        using Mapping.RegionCache<int[]> cache = new(16);
        for (int x = 0; x < 200; x++)
            Assert.NotNull(cache.GetOrAdd(x, 0, () => new int[1024]));

        Assert.InRange(cache.Count, 1, 16);
    }

    [Fact]
    public void Clearing_drops_every_region()
    {
        using Mapping.RegionCache<object> cache = new(16);
        _ = cache.GetOrAdd(1, 1, () => new object());
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }
}
