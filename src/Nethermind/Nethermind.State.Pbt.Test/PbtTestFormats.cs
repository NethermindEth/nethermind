// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

/// <summary>What a fixture needs to name a four-level layout by its group format.</summary>
internal static class PbtTestFormats
{
    /// <summary>The four-level layout storing the levels <paramref name="groupFormat"/> does.</summary>
    /// <exception cref="ArgumentOutOfRangeException">No four-level layout stores those levels.</exception>
    public static PbtTrieLayout FourLevel(PbtGroupFormat groupFormat) => groupFormat switch
    {
        PbtGroupFormat.EveryLevel => PbtTrieLayout.FourLevelEveryLevel,
        PbtGroupFormat.Interleaved => PbtTrieLayout.FourLevelInterleaved,
        PbtGroupFormat.BoundaryOnly => PbtTrieLayout.FourLevelBoundaryOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(groupFormat)),
    };
}
