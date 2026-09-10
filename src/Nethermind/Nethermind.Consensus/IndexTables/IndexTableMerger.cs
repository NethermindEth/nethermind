// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// Merges multiple sorted <see cref="IndexEntry"/> lists into a single sorted list.
/// </summary>
/// <remarks>
/// EIP-8304 higher-level tables are formed by merging 4 lower-level tables.
/// Each lower-level table is already sorted; this class performs a k-way merge
/// maintaining the lexicographic ordering required by the spec.
/// <para>See <see href="https://eips.ethereum.org/EIPS/eip-8304">EIP-8304</see>.</para>
/// </remarks>
public static class IndexTableMerger
{
    /// <summary>
    /// Merges multiple sorted entry lists into a single sorted list.
    /// </summary>
    /// <param name="sources">The sorted entry lists to merge. Each must already be sorted.</param>
    /// <returns>A new list containing all entries from all sources in sorted order.</returns>
    public static List<IndexEntry> Merge(IReadOnlyList<IReadOnlyList<IndexEntry>> sources)
    {
        // Total entry count across all sources
        int totalCount = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            totalCount += sources[i].Count;
        }

        if (totalCount == 0)
            return [];

        // For 4-way merge (typical case), a simple index-tracking approach is efficient
        List<IndexEntry> result = new(totalCount);
        int[] indices = new int[sources.Count];

        for (int processed = 0; processed < totalCount; processed++)
        {
            int minSource = -1;
            IndexEntry minEntry = default;

            for (int s = 0; s < sources.Count; s++)
            {
                if (indices[s] >= sources[s].Count)
                    continue;

                IndexEntry candidate = sources[s][indices[s]];
                if (minSource == -1 || candidate.CompareTo(minEntry) < 0)
                {
                    minSource = s;
                    minEntry = candidate;
                }
            }

            result.Add(minEntry);
            indices[minSource]++;
        }

        return result;
    }
}
