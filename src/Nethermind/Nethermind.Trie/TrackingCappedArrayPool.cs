// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Trie;

/// <summary>
/// Track every rented CappedArray<byte> and return them all at once
/// </summary>
public sealed class TrackingCappedArrayPool(int initialCapacity, ArrayPool<byte>? arrayPool = null, bool canBeParallel = true) : ICappedArrayPool, IDisposable
{
    private readonly ConcurrentQueue<byte[]>? _rentedQueue = canBeParallel ? new() : null;
    private readonly List<byte[]>? _rentedList = canBeParallel ? null : new(initialCapacity);
    private readonly ArrayPool<byte>? _arrayPool = arrayPool;

    public TrackingCappedArrayPool() : this(0)
    {
    public CappedArray<byte> Rent(int size)
    {
        if (size == 0)
        {
            return CappedArray<byte>.Empty;
        }

        byte[] array = arrayPool?.Rent(size) ?? SafeArrayPool<byte>.Shared.Rent(size);
        CappedArray<byte> rented = new(array, size);
        array.AsSpan().Clear();
        if (_rentedQueue is not null)
        {
            _rentedQueue.Enqueue(array);
        }
        else
        {
            _rentedList.Add(array);
        }
        return rented;
    }

    public void Return(in CappedArray<byte> buffer)
    {
    }

    public void Dispose()
    {
        if (_arrayPool is null)
        {
            DisposeCustomArrayPool();
            return;
        }

        ConcurrentQueue<byte[]>? rentedQueue = _rentedQueue;
        if (rentedQueue is not null)
        {
            while (rentedQueue.TryDequeue(out byte[]? rentedBuffer))
            {
                // Devirtualize shared array pool by referring directly to it
                SafeArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
        else
        {
            Span<byte[]> items = CollectionsMarshal.AsSpan(_rentedList);
            foreach (byte[] rentedBuffer in items)
            {
                SafeArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DisposeCustomArrayPool()
    {
        ArrayPool<byte> arrayPool = _arrayPool;
        if (_rentedQueue is not null)
        {
            while (_rentedQueue.TryDequeue(out byte[]? rentedBuffer))
            {
                arrayPool.Return(rentedBuffer);
            }
        }
        else
        {
            Span<byte[]> items = CollectionsMarshal.AsSpan(_rentedList);
            foreach (byte[] rentedBuffer in items)
            {
                arrayPool.Return(rentedBuffer);
            }
        }
    }
}
