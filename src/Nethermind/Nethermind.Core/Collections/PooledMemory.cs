// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Core.Collections;

public readonly struct PooledMemory<T>(ArrayPool<T> arrayPool, int length) : IDisposable, IEnumerable<T>
{
    public readonly T[] _array = arrayPool.Rent(length);
    public readonly int _length = length;
    public PooledMemory(int length) : this(ArrayPool<T>.Shared, length) { }

    public readonly int Length => _length;
    public readonly ref T this[int index]
    {
        get
        {
            if (index >= _length)
            {
                ThrowArgumentOutOfRangeException();
            }

            return ref _array[index];

            [DoesNotReturn]
            static void ThrowArgumentOutOfRangeException()
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
    }

    public static implicit operator Span<T>(PooledMemory<T> arrayPoolSpan) => arrayPoolSpan._array.AsSpan(0, arrayPoolSpan._length);
    public static implicit operator ReadOnlySpan<T>(PooledMemory<T> arrayPoolSpan) => arrayPoolSpan._array.AsSpan(0, arrayPoolSpan._length);

    public Span<T> Slice(int start, int length) => _array.AsSpan(start, length);

    public readonly void Dispose() => arrayPool.Return(_array);

    public IEnumerator<T> GetEnumerator() => new PooledArrayEnumerator<T>(_array, _length);

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
