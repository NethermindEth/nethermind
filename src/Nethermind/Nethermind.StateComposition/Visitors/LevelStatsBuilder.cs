// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Immutable;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Visitors;

/// <summary>
/// Shared builder for <see cref="TrieLevelStat"/> output. Hides the Geth-convention
/// <c>+1</c> depth shift (Geth reports <c>valueNode</c> one level below its leaf
/// <c>shortNode</c>) so the three call sites — full-scan depth distribution, Top-N
/// ranking scratch, single-contract inspection — cannot drift from each other.
/// </summary>
internal static class LevelStatsBuilder
{
    public static TrieLevelStat Fill(ReadOnlySpan<DepthCounter> depths, Span<TrieLevelStat> dest)
    {
        long summaryShort = 0, summaryFull = 0, summaryValue = 0, summarySize = 0;
        for (int i = 0; i < depths.Length; i++)
        {
            long shiftedValue = i > 0 ? depths[i - 1].ValueNodes : 0;
            dest[i] = new TrieLevelStat
            {
                Depth = i,
                FullNodeCount = depths[i].FullNodes,
                ShortNodeCount = depths[i].ShortNodes + depths[i].ValueNodes,
                ValueNodeCount = shiftedValue,
                TotalSize = depths[i].TotalSize,
            };
            summaryShort += depths[i].ShortNodes + depths[i].ValueNodes;
            summaryFull += depths[i].FullNodes;
            summaryValue += depths[i].ValueNodes;
            summarySize += depths[i].TotalSize;
        }

        return new TrieLevelStat
        {
            Depth = -1,
            ShortNodeCount = summaryShort,
            FullNodeCount = summaryFull,
            ValueNodeCount = summaryValue,
            TotalSize = summarySize,
        };
    }

    public static ImmutableArray<TrieLevelStat> BuildCompact(ReadOnlySpan<DepthCounter> depths)
    {
        Span<TrieLevelStat> scratch = stackalloc TrieLevelStat[depths.Length];
        int n = 0;
        for (int i = 0; i < depths.Length; i++)
        {
            long shiftedValue = i > 0 ? depths[i - 1].ValueNodes : 0;
            if (depths[i].FullNodes + depths[i].ShortNodes + depths[i].ValueNodes == 0
                && shiftedValue == 0)
                continue;

            scratch[n++] = new TrieLevelStat
            {
                Depth = i,
                FullNodeCount = depths[i].FullNodes,
                ShortNodeCount = depths[i].ShortNodes + depths[i].ValueNodes,
                ValueNodeCount = shiftedValue,
                TotalSize = depths[i].TotalSize,
            };
        }

        return [.. scratch[..n]];
    }
}
