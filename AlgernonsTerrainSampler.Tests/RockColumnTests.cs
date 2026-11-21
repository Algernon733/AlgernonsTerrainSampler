using AlgernonsTerrainSampler.Terrain;

namespace AlgernonsTerrainSampler.Tests;

/// <summary>
/// The top rock's column walk (<see cref="RockColumn"/>) against vanilla's <c>GenRockStrataNew.genBlockColumn</c>, written
/// out here as the game has it: a whole column of block ids, filled stratum by stratum, then its top block read.
/// </summary>
public class RockColumnTests
{
    private const int DefaultRock = 1;
    private const int SeaLevel = 110;

    // Vanilla's loop, line for line, over a column of ids: default rock everywhere, each stratum writing over default only.
    private static int VanillaTop(int height, RockStratumShape[] strata, int[] blockIds, float[] thickness, float[] groupMax, float distort)
    {
        int[] column = new int[height + 1];
        Array.Fill(column, DefaultRock);
        float[] current = new float[4];
        int bottom = 1;
        int top = height;
        int index = -1;
        RockStratumShape stratum = default;
        float remaining = 0f;
        while (bottom <= top)
        {
            if ((remaining -= 1f) <= 0f)
            {
                index++;
                if (index >= strata.Length)
                    break;

                stratum = strata[index];
                remaining = Math.Min((groupMax[stratum.Group] * distort) - current[stratum.Group], thickness[index]);
                if (stratum.Sedimentary)
                    remaining -= Math.Max(0, top - SeaLevel) * 0.5f;

                if (remaining < 2f)
                {
                    remaining = -1f;
                    continue;
                }

                if (blockIds[index] == DefaultRock)
                {
                    int skipped = (int)remaining;
                    current[stratum.Group] += skipped;
                    if (stratum.TopDown)
                        top -= skipped;
                    else
                        bottom += skipped;

                    continue;
                }
            }

            current[stratum.Group]++;
            if (!stratum.TopDown)
            {
                if (column[bottom] == DefaultRock)
                    column[bottom] = blockIds[index];

                bottom++;
            }
            else
            {
                if (column[top] == DefaultRock)
                    column[top] = blockIds[index];

                top--;
            }
        }

        return column[height];
    }

    private static (RockStratumShape[] Strata, int[] Ids) Strata(Random random)
    {
        int count = random.Next(1, 9);
        RockStratumShape[] strata = new RockStratumShape[count];
        int[] ids = new int[count];
        for (int i = 0; i < count; i++)
        {
            int group = random.Next(4);
            ids[i] = random.Next(5) == 0 ? DefaultRock : 10 + i;
            strata[i] = new RockStratumShape(random.Next(2) == 0, group, group == 0, ids[i] == DefaultRock);
        }

        return (strata, ids);
    }

    [Fact]
    public void The_walk_finds_the_block_vanilla_leaves_on_top_of_the_column()
    {
        Random random = new(1234);
        int topDown = 0;
        int bottomUp = 0;
        int untouched = 0;
        for (int trial = 0; trial < 20000; trial++)
        {
            (RockStratumShape[] strata, int[] ids) = Strata(random);
            int height = random.Next(40, 250);
            float[] thickness = new float[strata.Length];
            for (int i = 0; i < thickness.Length; i++)
                thickness[i] = (float)(random.NextDouble() * 80.0);

            float[] groupMax = [(float)(random.NextDouble() * 120), (float)(random.NextDouble() * 120), (float)(random.NextDouble() * 200), (float)(random.NextDouble() * 60)];
            float distort = 0.9f + ((float)random.NextDouble() * 0.2f);

            int expected = VanillaTop(height, strata, ids, thickness, groupMax, distort);
            int index = RockColumn.TopStratum(height, SeaLevel, strata, k => thickness[k], groupMax, distort);
            int actual = index < 0 ? DefaultRock : ids[index];
            Assert.Equal(expected, actual);

            if (index < 0 || ids[index] == DefaultRock)
                untouched++;
            else if (strata[index].TopDown)
                topDown++;
            else
                bottomUp++;
        }

        // Every way a top block can end up is exercised.
        Assert.True(topDown > 1000 && bottomUp > 100 && untouched > 100, $"top-down {topDown}, bottom-up {bottomUp}, default {untouched}");
    }

    [Fact]
    public void A_sedimentary_cap_thins_with_height_above_the_sea()
    {
        RockStratumShape[] strata = [new(TopDown: true, Group: 0, Sedimentary: true, DefaultRock: false), new(TopDown: false, Group: 2, Sedimentary: false, DefaultRock: false)];
        float[] groupMax = [40f, 0f, 200f, 0f];

        // Twelve blocks of sandstone: a lowland column keeps it, one twenty blocks up loses it to what is below.
        Assert.Equal(0, RockColumn.TopStratum(SeaLevel + 4, SeaLevel, strata, k => k == 0 ? 12f : 300f, groupMax, 1f));
        Assert.Equal(1, RockColumn.TopStratum(SeaLevel + 24, SeaLevel, strata, k => k == 0 ? 12f : 300f, groupMax, 1f));
    }

    [Fact]
    public void No_strata_leave_the_default_rock()
        => Assert.Equal(-1, RockColumn.TopStratum(120, SeaLevel, [], _ => 50f, [10f, 10f, 10f, 10f], 1f));
}
