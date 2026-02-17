// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Trie;

/// <summary>
/// Track every rented CappedArray<byte> and return them all at once
/// </summary>
public sealed class TrackingCappedArrayPool(int initialCapacity, ArrayPool<byte>? arrayPool = null)
    : ICappedArrayPool, IDisposable
{
    private readonly List<byte[]> _rentedBuffers = new(initialCapacity);

    public TrackingCappedArrayPool() : this(0)
    {
    }

    public CappedArray<byte> Rent(int size)
    {
        if (size == 0)
        {
            return CappedArray<byte>.Empty;
        }

        byte[] array = arrayPool?.Rent(size) ?? SafeArrayPool<byte>.Shared.Rent(size);
        CappedArray<byte> rented = new(array, size);
        array.AsSpan().Clear();
        lock (_rentedBuffers) _rentedBuffers.Add(array);
        return rented;
    }

    public void Return(in CappedArray<byte> buffer)
    {
    }

    public void Dispose()
    {
        if (arrayPool is null)
        {
            foreach (byte[] rentedBuffer in CollectionsMarshal.AsSpan(_rentedBuffers))
            {
                SafeArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
        else
        {
            foreach (byte[] rentedBuffer in CollectionsMarshal.AsSpan(_rentedBuffers))
            {
                arrayPool.Return(rentedBuffer);
            }
        }
    }
}
