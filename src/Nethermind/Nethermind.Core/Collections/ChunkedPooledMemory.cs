// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Core.Collections;

public readonly struct ChunkedPooledMemory<T>(ArrayPool<T> arrayPool, int chunkCount, int chunkSize) : IDisposable, IEnumerable<Memory<T>>
{
    internal readonly T[] _array = arrayPool.Rent(chunkCount * chunkSize);

    public ChunkedPooledMemory(int chunkCount, int chunkSize) : this(ArrayPool<T>.Shared, chunkCount, chunkSize) { }

    private struct PooledArrayEnumerator(T[] array, int chunkCount, int chunkSize) : IEnumerator<Memory<T>>
    {
        private int _index = -1;

        public bool MoveNext() => ++_index < chunkCount;

        public void Reset() => _index = -1;

        public readonly Memory<T> Current => array.AsMemory(_index * chunkSize, chunkSize);

        readonly object IEnumerator.Current => Current!;

        public readonly void Dispose() { }
    }

    public readonly Span<T> this[int index]
    {
        get
        {
            if (index * chunkSize >= chunkCount)
            {
                ThrowArgumentOutOfRangeException();
            }

            return _array.AsSpan(index * chunkSize, chunkSize);

            [DoesNotReturn]
            static void ThrowArgumentOutOfRangeException()
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }

    public Span<T> this[Range range]
    {
        get
        {
            (int offset, int length) = GetOffsetAndLength(range);
            if (offset < 0 || length < 0 || offset + length > chunkCount)
                throw new ArgumentOutOfRangeException(nameof(range), "Range is out of bounds.");
            return _array.AsSpan(offset * chunkSize, length * chunkSize);
        }
    }

    private (int Offset, int Length) GetOffsetAndLength(Range range)
    {
        var start = range.Start.IsFromEnd ? chunkCount - range.Start.Value : range.Start.Value;
        var end = range.End.IsFromEnd ? chunkCount - range.End.Value : range.End.Value;
        return (start, end - start);
    }

    public IEnumerator<Memory<T>> GetEnumerator() => new PooledArrayEnumerator(_array, chunkCount, chunkSize);

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Dispose() => arrayPool.Return(_array);
    public int Length => chunkCount;
}
