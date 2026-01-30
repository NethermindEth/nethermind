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

        // Devirtualize shared array pool by referring directly to it
        byte[] array = arrayPool?.Rent(size) ?? ArrayPool<byte>.Shared.Rent(size);
        CappedArray<byte> rented = new(array, size);
        array.AsSpan().Clear();
        lock (_rentedBuffers) _rentedBuffers.Add(array);
        return rented;
    }

    public void Return(in CappedArray<byte> buffer) { }

    public void Dispose()
    {
        // NativeAOT/ZKVM: ArrayPool<T>.Shared.Return may throw in constrained runtimes (e.g. missing CPU/threading features).
        // We intentionally skip returning buffers to avoid bringing down the process during disposal paths.

#if !ZKVM
        if (arrayPool is not null)
        {
            foreach (byte[] rentedBuffer in CollectionsMarshal.AsSpan(_rentedBuffers))
            {
                arrayPool.Return(rentedBuffer);
            }
        }
        else
        {
            foreach (byte[] rentedBuffer in CollectionsMarshal.AsSpan(_rentedBuffers))
            {
                // Devirtualize shared array pool by referring directly to it
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
#endif
    }
}
