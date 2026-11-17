// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;

namespace Nethermind.Core.Collections;

/// <summary>Holds copies of byte items in pooled arrays of a fixed size, so no rental grows with the total size.</summary>
/// <remarks>Each item stays contiguous: an item that does not fit the rest of the current segment starts a new one, and an
/// item larger than a segment gets a rental of its own. Not thread-safe.</remarks>
/// <param name="segmentSize">The size of the arrays that small items are copied into.</param>
public sealed class PooledSegmentedBuffer(int segmentSize) : IDisposable
{
    private readonly List<byte[]> _rentals = [];
    private byte[] _segment = [];
    private int _segmentLength;

    /// <summary>Copies <paramref name="item"/> into the buffer.</summary>
    /// <returns>The copy, valid until the buffer is disposed.</returns>
    public ReadOnlyMemory<byte> Add(ReadOnlySpan<byte> item)
    {
        if (item.Length > segmentSize) return Copy(item, Rent(item.Length), 0);

        if (item.Length > _segment.Length - _segmentLength)
        {
            _segment = Rent(segmentSize);
            _segmentLength = 0;
        }

        ReadOnlyMemory<byte> copy = Copy(item, _segment, _segmentLength);
        _segmentLength += item.Length;
        return copy;
    }

    private static ReadOnlyMemory<byte> Copy(ReadOnlySpan<byte> item, byte[] array, int start)
    {
        item.CopyTo(array.AsSpan(start));
        return array.AsMemory(start, item.Length);
    }

    private byte[] Rent(int length)
    {
        byte[] array = SafeArrayPool<byte>.Shared.Rent(length);
        _rentals.Add(array);
        return array;
    }

    /// <summary>Returns the rented arrays to the pool, which invalidates every copy.</summary>
    public void Dispose()
    {
        foreach (byte[] array in _rentals) SafeArrayPool<byte>.Shared.Return(array);
        _rentals.Clear();
        _segment = [];
        _segmentLength = 0;
    }
}
