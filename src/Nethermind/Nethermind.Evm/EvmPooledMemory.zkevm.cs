// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if ZK_EVM
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Collections;

namespace Nethermind.Evm;

public partial struct EvmPooledMemory
{
    private const int MaxCachedArrayLength = 1 << 16;
    private const int CleanCacheSlots = 16;

    [ThreadStatic] private static byte[]?[]? _cleanArrays;
    [ThreadStatic] private static int _cleanArrayCount;

    private static byte[] RentClean(int minLength)
    {
        byte[]?[]? cache = _cleanArrays;
        int cleanArrayCount = _cleanArrayCount - 1;
        for (int i = cleanArrayCount; i >= 0; i--)
        {
            byte[] candidate = cache![i]!;
            if (candidate.Length >= minLength)
            {
                _cleanArrayCount = cleanArrayCount;
                cache[i] = cache[cleanArrayCount];
                cache[cleanArrayCount] = null;
                return candidate;
            }
        }

        if (minLength > MaxCachedArrayLength)
        {
            byte[] pooled = RentLarge(minLength);
            Array.Clear(pooled);
            return pooled;
        }

        return new byte[BitOperations.RoundUpToPowerOf2((uint)minLength)];
    }

    private static void ReturnClean(byte[] array, int dirtyLength)
    {
        if (array.Length > MaxCachedArrayLength)
        {
            ReturnLarge(array);
            return;
        }

        byte[]?[] cache = _cleanArrays ??= new byte[CleanCacheSlots][];
        if (_cleanArrayCount < CleanCacheSlots)
        {
            Array.Clear(array, 0, dirtyLength);
            cache[_cleanArrayCount++] = array;
        }
    }

    private static byte[] RentLarge(int minLength) => SafeArrayPool<byte>.Shared.Rent(minLength);

    private static void ReturnLarge(byte[] array) => SafeArrayPool<byte>.Shared.Return(array);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RentSlow()
    {
        if (_memory is null)
        {
            _memory = RentClean((int)Math.Max((uint)Size, MinRentSize));
        }
        else if (Size > (ulong)_memory.LongLength)
        {
            byte[] beforeResize = _memory;
            _memory = RentClean(TruncateToInt32(Size));
            Array.Copy(beforeResize, 0, _memory, 0, beforeResize.Length);
            ReturnClean(beforeResize, beforeResize.Length);
        }

        _offset = 0; // the ZK arena buffer is per-frame, so the window always starts at index 0
        _lastZeroedSize = (ulong)_memory.Length;
    }
}
#endif
