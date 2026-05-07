// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Orders <see cref="Eip7928Constants.PrestateIndex"/> before every BAL access index,
/// preserving iteration order after prestate entries are grafted into decoded BALs.
/// </summary>
public sealed class PrestateAwareIndexComparer : IComparer<uint>
{
    public static PrestateAwareIndexComparer Instance { get; } = new();

    private PrestateAwareIndexComparer() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(uint x, uint y) => CompareIndices(x, y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompareIndices(uint x, uint y)
    {
        if (x == Eip7928Constants.PrestateIndex)
        {
            return y == Eip7928Constants.PrestateIndex ? 0 : -1;
        }
        if (y == Eip7928Constants.PrestateIndex)
        {
            return 1;
        }
        return x.CompareTo(y);
    }
}
