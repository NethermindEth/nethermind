// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>What a fixture needs to name a layout: the four-level ones by their levels, and why the eight-level ones are skipped.</summary>
internal static class PbtTestFormats
{
    /// <summary>
    /// The eight-level fold is unfinished: its 256-slot boundary needs four mask words, and the shape
    /// the fold merges the untouched slots from — <c>PbtTrieNodeGroup.BoundaryShape</c> and
    /// <c>GroupShape.MergeUntouched</c> — still carries one, so every fold over that tiling throws.
    /// </summary>
    public const string EightLevelFoldUnfinished =
        "the eight-level fold is unfinished: PbtTrieNodeGroup.BoundaryShape throws on a boundary wider than one mask word";


    /// <summary>The four-level clustered layout storing the levels <paramref name="groupFormat"/> does.</summary>
    /// <exception cref="ArgumentOutOfRangeException">No clustered layout stores those levels.</exception>
    public static PbtTrieLayout Clustered(PbtGroupFormat groupFormat) => groupFormat switch
    {
        PbtGroupFormat.EveryLevel => PbtTrieLayout.ClusteredFourLevelEveryLevel,
        PbtGroupFormat.Interleaved => PbtTrieLayout.ClusteredFourLevelInterleaved,
        PbtGroupFormat.BoundaryOnly => PbtTrieLayout.ClusteredFourLevelBoundaryOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(groupFormat)),
    };
}
