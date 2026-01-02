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
public sealed class TrackingCappedArrayPool : ICappedArrayPool, IDisposable
{
    private readonly List<byte[]> _rentedBuffers;
    private readonly ArrayPool<byte>? _arrayPool;

    public TrackingCappedArrayPool() : this(0)
    {
    }

    public TrackingCappedArrayPool(int initialCapacity, ArrayPool<byte> arrayPool = null)
    {
        _rentedBuffers = new List<byte[]>(initialCapacity);
        _arrayPool = arrayPool;
    }

    public CappedArray<byte> Rent(int size)
    {
        if (size == 0)
        {
            return CappedArray<byte>.Empty;
        }

        // Devirtualize shared array pool by referring directly to it
        byte[] array = _arrayPool?.Rent(size) ?? ArrayPool<byte>.Shared.Rent(size);
        CappedArray<byte> rented = new CappedArray<byte>(array, size);
        array.AsSpan().Clear();
        lock (_rentedBuffers) _rentedBuffers.Add(array);
        return rented;
    }

    public void Return(in CappedArray<byte> buffer)
    {
    }

    public void Dispose()
    {
        if (_arrayPool is null)
        {
            foreach (byte[] rentedBuffer in CollectionsMarshal.AsSpan(_rentedBuffers))
            {
                // Devirtualize shared array pool by referring directly to it
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
        else
        {
            foreach (byte[] rentedBuffer in CollectionsMarshal.AsSpan(_rentedBuffers))
            {
                _arrayPool.Return(rentedBuffer);
            }
        }
    }
}
