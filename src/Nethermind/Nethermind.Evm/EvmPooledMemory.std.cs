// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Evm;

public partial struct EvmPooledMemory
{
    private const int MaxSharedArrayLength = 1 << 20;
    // Buffers above this limit are allocated directly and are never returned to a pool.
    private const int MaxLargePooledArrayLength = 1 << 22;
    private static readonly System.Buffers.ArrayPool<byte> _largeArrayPool =
        System.Buffers.ArrayPool<byte>.Create(maxArrayLength: MaxLargePooledArrayLength, maxArraysPerBucket: 16);

    private static byte[] RentLarge(int minLength, out ulong initializedSize)
    {
        if (minLength > MaxLargePooledArrayLength)
        {
            byte[] fresh = new byte[ArrayPoolUtilities.GetPowerOfTwoCapacity(minLength)];
            initializedSize = (ulong)fresh.Length;
            return fresh;
        }

        initializedSize = 0;
        return minLength > MaxSharedArrayLength
            ? _largeArrayPool.Rent(minLength)
            : SafeArrayPool<byte>.Shared.Rent(minLength);
    }

    private static void ReturnLarge(byte[] array)
    {
        if (array.Length > MaxLargePooledArrayLength)
        {
            return;
        }

        if (array.Length > MaxSharedArrayLength)
            _largeArrayPool.Return(array);
        else
            SafeArrayPool<byte>.Shared.Return(array);
    }
}
