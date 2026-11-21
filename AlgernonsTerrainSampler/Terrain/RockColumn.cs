#nullable enable
using System;

namespace AlgernonsTerrainSampler.Terrain;

/// <summary>One rock stratum as the column walk needs it: which way it fills, its rock group, and whether it is the world's default rock.</summary>
/// <param name="TopDown">The stratum fills from the column's top down (<c>EnumStratumGenDir.TopDown</c>); otherwise from the bottom up.</param>
/// <param name="Group">Its <c>EnumRockGroup</c> as an index, 0 to 3.</param>
/// <param name="Sedimentary">Its group is sedimentary, which thins with the column's height above sea level.</param>
/// <param name="DefaultRock">Its block is the world's default rock, which the walk skips over rather than writes.</param>
public readonly record struct RockStratumShape(bool TopDown, int Group, bool Sedimentary, bool DefaultRock);

/// <summary>
/// Vanilla's rock strata for one block column (<c>GenRockStrataNew.genBlockColumn</c>), followed only as far as the
/// column's top block: which stratum's rock ends up there. That block is the top rock worldgen's block layers stand on
/// (<c>GenBlockLayers</c> records it in <c>TopRockIdMap</c> and fills <c>{rocktype}</c> from it). No game types, so it is
/// tested without the game.
/// </summary>
/// <remarks>
/// <para>
/// The column runs from y 1 to its terrain height. Strata are taken in order; each gets a thickness, the smaller of what
/// its group has left in the column's geologic province and its own noise map there, less half the column's height
/// above sea level for a sedimentary one. A stratum under two blocks is skipped. A top-down stratum fills from the top,
/// a bottom-up one from the bottom; the default rock is skipped over, not written. The walk ends when the two meet or
/// the strata run out. The top block is whichever stratum is written there first; untouched, it stays the default rock.
/// </para>
/// </remarks>
public static class RockColumn
{
    /// <summary>
    /// The index into <paramref name="strata"/> of the stratum whose rock is the column's top block, or -1 for the
    /// world's default rock.
    /// </summary>
    /// <param name="height">The column's terrain height, its top block's y.</param>
    /// <param name="seaLevel">The world's sea level.</param>
    /// <param name="strata">The strata, in the order vanilla takes them.</param>
    /// <param name="thickness">A stratum's thickness from its noise map at this column, asked once for each stratum reached.</param>
    /// <param name="groupMax">Each rock group's most in this column, from its provinces' weights: four values.</param>
    /// <param name="distort">The column's thickness factor, 0.9 to 1.1.</param>
    public static int TopStratum(int height, int seaLevel, ReadOnlySpan<RockStratumShape> strata, Func<int, float> thickness, ReadOnlySpan<float> groupMax, float distort)
    {
        ArgumentNullException.ThrowIfNull(thickness);
        Span<int> used = stackalloc int[4];
        int bottom = 1;
        int top = height;
        int index = -1;
        int group = 0;
        bool topDown = false;
        float remaining = 0f;
        while (bottom <= top)
        {
            if ((remaining -= 1f) <= 0f)
            {
                index++;
                if (index >= strata.Length)
                    break;

                RockStratumShape stratum = strata[index];
                group = stratum.Group;
                topDown = stratum.TopDown;
                remaining = Math.Min((groupMax[group] * distort) - used[group], thickness(index));
                if (stratum.Sedimentary)
                    remaining -= Math.Max(0, top - seaLevel) * 0.5f;

                if (remaining < 2f)
                {
                    remaining = -1f;
                    continue;
                }

                if (stratum.DefaultRock)
                {
                    // Skipped over, left as it is: if that covers the top block, nothing else reaches it.
                    int skipped = (int)remaining;
                    used[group] += skipped;
                    if (topDown)
                    {
                        if (top == height)
                            return -1;

                        top -= skipped;
                    }
                    else
                    {
                        if (bottom <= height && bottom + skipped > height)
                            return -1;

                        bottom += skipped;
                    }

                    continue;
                }
            }

            used[group]++;
            if (topDown)
            {
                if (top == height)
                    return index;

                top--;
            }
            else
            {
                if (bottom == height)
                    return index;

                bottom++;
            }
        }

        return -1;
    }
}
