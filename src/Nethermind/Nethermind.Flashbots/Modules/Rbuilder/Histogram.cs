// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Flashbots.Modules.Rbuilder;

using System;

/// <summary>
/// A simple histogram specifically optimized for integer values
/// in the exact range of 0 to 100 (inclusive).
/// It provides exact percentile calculations for the stored discrete values.
/// </summary>
internal class Histogram
{
    private const int MinValue = 0;
    private const int MaxValue = 100;
    private const int RangeSize = MaxValue - MinValue + 1;

    private readonly long[] _buckets;
    private long _totalCount;


    internal Histogram()
    {
        _buckets = new long[RangeSize];
        _totalCount = 0;
    }

    /// <summary>
    /// Adds an integer value (expected 0-100) to the histogram.
    /// Values outside the range [0, 100] will be clamped to the nearest boundary.
    /// </summary>
    /// <param name="value">The integer value to add.</param>
    internal void Add(int value)
    {
        int index;
        // Clamp the value to the valid range [0, 100]
        if (value < MinValue)
        {
            index = MinValue;
        }
        else if (value > MaxValue)
        {
            index = MaxValue;
        }
        else
        {
            index = value;
        }

        _buckets[index]++;
        _totalCount++;
    }

    /// <summary>
    /// Calculates the integer value at a given percentile.
    /// </summary>
    /// <param name="percentile">The percentile to calculate (e.g., 0.50 for P50, 0.90 for P90).</param>
    /// <returns>The integer value at the specified percentile. Returns 0 if no values have been added.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if percentile is not between 0.0 and 1.0.</exception>
    internal int GetPercentile(double percentile)
    {
        if (percentile < 0.0 || percentile > 1.0)
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be between 0.0 and 1.0.");

        if (_totalCount == 0)
            return MinValue; // Return the minimum possible value if empty

        // Calculate the target rank (which data point number we are looking for)
        // Using Ceiling ensures that for p=1.0 we target the last element.
        var targetRank = (long)Math.Ceiling(_totalCount * percentile);
        long currentCumulativeCount = 0;

        for (var value = MinValue; value <= MaxValue; value++)
        {
            currentCumulativeCount += _buckets[value];
            if (currentCumulativeCount >= targetRank)
            {
                // This 'value' is the first value where the cumulative count
                // reaches or exceeds the target rank. This is our percentile value.
                return value;
            }
        }

        return MaxValue;
    }


    public int GetP50() => GetPercentile(0.50);
    public int GetP90() => GetPercentile(0.90);
    public int GetP99() => GetPercentile(0.99);
    public long Count() => _totalCount;
}
