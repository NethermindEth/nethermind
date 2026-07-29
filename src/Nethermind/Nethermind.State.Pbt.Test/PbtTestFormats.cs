// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>What a fixture needs to name a layout: the four-level ones by their levels.</summary>
internal static class PbtTestFormats
{
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
