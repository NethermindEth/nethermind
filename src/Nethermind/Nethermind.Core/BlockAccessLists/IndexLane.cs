// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Binary-search helpers over a parallel <c>uint</c> index array and a values array of the same
/// length. Shared between <see cref="AccountIndexLane"/> (account-level balance/nonce/code lanes)
/// and <see cref="ReadOnlySlotChanges"/> (per-slot storage changes).
/// </summary>
internal static class IndexLane
{
    /// <summary>Entry from <paramref name="values"/> at exactly <c>Index == index</c>, or null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T? GetExact<T>(ReadOnlySpan<uint> indices, T[] values, uint index) where T : struct
    {
        int idx = indices.BinarySearch(index);
        return idx >= 0 ? values[idx] : null;
    }

    /// <summary>Out-pattern variant of <see cref="GetExact{T}"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetExact<T>(ReadOnlySpan<uint> indices, T[] values, uint index, out T value) where T : struct
    {
        int idx = indices.BinarySearch(index);
        if (idx >= 0)
        {
            value = values[idx];
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>True iff <paramref name="indices"/> contains <paramref name="index"/> exactly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasExact(ReadOnlySpan<uint> indices, uint index)
        => indices.BinarySearch(index) >= 0;

    /// <summary>
    /// Entry with the largest <c>Index</c> strictly less than <paramref name="index"/>; returns
    /// <c>false</c> if none.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetLastBefore<T>(ReadOnlySpan<uint> indices, T[] values, uint index, out T last) where T : struct
    {
        int idx = indices.BinarySearch(index);
        // idx (if found) or ~idx (if not) is the position of the first entry with Index >= target;
        // strictly-before is one step earlier.
        int lastBefore = (idx >= 0 ? idx : ~idx) - 1;
        if (lastBefore < 0)
        {
            last = default;
            return false;
        }
        last = values[lastBefore];
        return true;
    }
}
