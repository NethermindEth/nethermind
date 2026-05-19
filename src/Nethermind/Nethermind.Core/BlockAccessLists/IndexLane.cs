// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Binary-search helpers over a parallel <c>uint</c> index array and an equal-length values
/// array. Shared between <see cref="AccountIndexLane"/> and <see cref="ReadOnlySlotChanges"/>.
/// </summary>
internal static class IndexLane
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T? GetExact<T>(ReadOnlySpan<uint> indices, T[] values, uint index) where T : struct
    {
        int idx = indices.BinarySearch(index);
        return idx >= 0 ? values[idx] : null;
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasExact(ReadOnlySpan<uint> indices, uint index)
        => indices.BinarySearch(index) >= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetLastBefore<T>(ReadOnlySpan<uint> indices, T[] values, uint index, out T last) where T : struct
    {
        int idx = indices.BinarySearch(index);
        // BinarySearch returns idx if found, else ~idx (insertion point); strictly-before is one earlier.
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
