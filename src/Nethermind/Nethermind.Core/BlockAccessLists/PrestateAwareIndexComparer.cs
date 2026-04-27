// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Orders <see cref="uint"/> BAL access indices with <see cref="Eip7928Constants.PrestateIndex"/>
/// sorted ahead of every real index. Used for BALs that may later receive prestate entries so
/// that entries added by <c>BlockAccessListManager.LoadPreState</c> appear first in iteration —
/// matching the semantics of the legacy <c>-1</c> int sentinel before BlockAccessIndex was
/// widened to uint32. Decoded BALs (from the wire) also use this comparer even though they do
/// not initially contain a prestate entry, so later prestate grafting preserves sorted order.
/// </summary>
public sealed class PrestateAwareIndexComparer : IComparer<uint>
{
    public static readonly PrestateAwareIndexComparer Instance = new();

    private PrestateAwareIndexComparer() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(uint x, uint y)
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
