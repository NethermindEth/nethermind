// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;

namespace Nethermind.Tools.Kute.Replay;

/// <summary>Parses the concurrency levels of a sweep from its command-line form.</summary>
public static class ConcurrencySpec
{
    /// <summary>
    /// Highest level accepted. Well past anything useful, and low enough that the doubling progression
    /// cannot overflow.
    /// </summary>
    public const int MaxLevel = 100_000;

    /// <summary>
    /// Expands a concurrency specification into the levels to run.
    /// </summary>
    /// <param name="spec">
    /// Either a range such as <c>1-64</c>, which expands to doubling levels up to and including the
    /// bound, or an explicit comma-separated list such as <c>1,4,12,50</c>, or a single level.
    /// </param>
    /// <returns>The levels to run, in ascending order, without duplicates.</returns>
    /// <exception cref="FormatException">The specification is not a level, list or range.</exception>
    /// <remarks>
    /// A range doubles rather than stepping by one: sweeping every integer to 64 would run 64 levels
    /// where 7 show the same shape, and each level costs a full pass over the trace prefix.
    /// </remarks>
    public static IReadOnlyList<int> Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);

        string trimmed = spec.Trim();
        int dash = trimmed.IndexOf('-', StringComparison.Ordinal);

        SortedSet<int> levels = [];
        if (dash > 0)
        {
            int from = ParseLevel(trimmed[..dash]);
            int to = ParseLevel(trimmed[(dash + 1)..]);
            if (from > to)
            {
                throw new FormatException($"Concurrency range '{trimmed}' runs backwards.");
            }

            for (long level = from; level < to; level *= 2)
            {
                levels.Add((int)level);
            }

            levels.Add(to);
        }
        else
        {
            foreach (string part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                levels.Add(ParseLevel(part));
            }
        }

        if (levels.Count == 0)
        {
            throw new FormatException($"Concurrency specification '{spec}' names no levels.");
        }

        return [.. levels];
    }

    private static int ParseLevel(string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int level)
            || level < 1
            || level > MaxLevel)
        {
            throw new FormatException($"'{text}' is not a concurrency level between 1 and {MaxLevel}.");
        }

        return level;
    }
}
