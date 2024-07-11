// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Nethermind.Core.Buffers;

namespace Nethermind.Trie;

/// <summary>
/// Track every rented CappedArray<byte> and return them all at once
/// </summary>
public class TrackingCappedArrayPool : ICappedArrayPool, IDisposable
{
    private readonly List<byte[]> _rentedBuffers;
    private readonly ArrayPool<byte> _arrayPool;

    public TrackingCappedArrayPool() : this(0)
    {
    }

    public TrackingCappedArrayPool(int initialCapacity, ArrayPool<byte> arrayPool = null)
    {
        _rentedBuffers = new List<byte[]>(initialCapacity);
        _arrayPool = arrayPool ?? ArrayPool<byte>.Shared;
    }

    public CappedArray<byte> Rent(int size)
    {
        if (size == 0)
        {
            return CappedArray<byte>.Empty;
        }

        byte[] array = _arrayPool.Rent(size);
        CappedArray<byte> rented = new CappedArray<byte>(array, size);
        array.AsSpan().Clear();
        _rentedBuffers.Add(array);
        return rented;
    }

    public void Return(in CappedArray<byte> buffer)
    {
    }

    public void Dispose()
    {
        foreach (byte[] rentedBuffer in CollectionsMarshal.AsSpan(_rentedBuffers))
        {
            _arrayPool.Return(rentedBuffer);
        }
    }
}
