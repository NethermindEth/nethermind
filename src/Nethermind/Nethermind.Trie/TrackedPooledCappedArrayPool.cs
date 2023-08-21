// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

/// <summary>
/// Track every rented CappedArray<byte> and return them all at once
/// </summary>
public class TrackedPooledCappedArrayPool : ICappedArrayPool
{
    private List<CappedArray<byte>> _rentedBuffers;
    private ArrayPool<byte> _arrayPool;

    public TrackedPooledCappedArrayPool() : this(0)
    {
    }

    public TrackedPooledCappedArrayPool(int initialCapacity, ArrayPool<byte> arrayPool = null)
    {
        _rentedBuffers = new List<CappedArray<byte>>(initialCapacity);
        _arrayPool = arrayPool ?? ArrayPool<byte>.Shared;
    }

    public CappedArray<byte> Rent(int size)
    {
        var rented = new CappedArray<byte>(_arrayPool.Rent(size), size);
        rented.AsSpan().Fill(0);
        _rentedBuffers.Add(rented);
        return rented;
    }

    public void Return(CappedArray<byte> buffer)
    {
    }

    public void ReturnAll()
    {
        foreach (CappedArray<byte> rentedBuffer in _rentedBuffers)
        {
            _arrayPool.Return(rentedBuffer.Array);
        }
    }
}
