// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Core.Caching;

public abstract class ClockCacheBase<TKey>
    where TKey : struct, IEquatable<TKey>
{
    protected const int BitShiftPerInt64 = 6;

    protected int MaxCapacity { get; }

    protected TKey[] KeyToOffset { get; }
    protected long[] HasBeenAccessedBitmap { get; }
    protected Queue<int> FreeOffsets { get; } = new();

    protected int Clock { get; set; } = 0;

    protected ClockCacheBase(int maxCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);

        MaxCapacity = maxCapacity;
        KeyToOffset = new TKey[maxCapacity];
        HasBeenAccessedBitmap = new long[GetInt64ArrayLengthFromBitLength(maxCapacity)];
    }

    protected void Clear()
    {
        Clock = 0;
        FreeOffsets.Clear();
        KeyToOffset.AsSpan().Clear();
        HasBeenAccessedBitmap.AsSpan().Clear();
    }

    protected bool ClearAccessed(int position)
    {
        uint offset = (uint)position >> BitShiftPerInt64;
        long flags = 1L << position;

        ref long accessedBitmapWord = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(HasBeenAccessedBitmap), offset);
        bool accessed = (accessedBitmapWord & flags) != 0;

        if (accessed)
        {
            // Clear the accessed bit
            flags = ~flags;
            long current = Volatile.Read(ref accessedBitmapWord);
            while (true)
            {
                long previous = Interlocked.CompareExchange(ref accessedBitmapWord, current & flags, current);
                if (previous == current)
                {
                    break;
                }
                current = previous;
            }
        }

        return accessed;
    }

    protected void MarkAccessed(int position)
    {
        uint offset = (uint)position >> BitShiftPerInt64;
        long flags = 1L << position;

        ref long accessedBitmapWord = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(HasBeenAccessedBitmap), offset);

        long current = Volatile.Read(ref accessedBitmapWord);
        while (true)
        {
            long previous = Interlocked.CompareExchange(ref accessedBitmapWord, current | flags, current);
            if (previous == current)
            {
                break;
            }
            current = previous;
        }
    }

    /// <summary>
    /// Used for conversion between different representations of bit array.
    /// Returns (n + (64 - 1)) / 64, rearranged to avoid arithmetic overflow.
    /// For example, in the bit to int case, the straightforward calc would
    /// be (n + 63) / 64, but that would cause overflow. So instead it's
    /// rearranged to ((n - 1) / 64) + 1.
    /// Due to sign extension, we don't need to special case for n == 0, if we use
    /// bitwise operations (since ((n - 1) >> 6) + 1 = 0).
    /// This doesn't hold true for ((n - 1) / 64) + 1, which equals 1.
    ///
    /// Usage:
    /// GetInt32ArrayLengthFromBitLength(77): returns how many ints must be
    /// allocated to store 77 bits.
    /// </summary>
    /// <param name="n"></param>
    /// <returns>how many ints are required to store n bytes</returns>
    private static int GetInt64ArrayLengthFromBitLength(int n) =>
        (n - 1 + (1 << BitShiftPerInt64)) >>> BitShiftPerInt64;
}
