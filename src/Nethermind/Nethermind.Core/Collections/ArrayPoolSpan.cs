// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System;
using System.Collections.Generic;
using System.Collections;

namespace Nethermind.Core.Collections;

public readonly struct ArrayPoolSpan<T>(ArrayPool<T> arrayPool, int length) : IDisposable, IEnumerable<T>
{
    private readonly T[] _array = arrayPool.Rent(length);
    private readonly int _length = length;
    public ArrayPoolSpan(int length) : this(SafeArrayPool<T>.Shared, length) { }

    public readonly int Length => _length;
    public readonly ref T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _length, nameof(index));
            return ref _array[index];
        }
    }

    public static implicit operator Span<T>(ArrayPoolSpan<T> arrayPoolSpan) => arrayPoolSpan._array.AsSpan(0, arrayPoolSpan._length);
    public static implicit operator ReadOnlySpan<T>(ArrayPoolSpan<T> arrayPoolSpan) => arrayPoolSpan._array.AsSpan(0, arrayPoolSpan._length);

    public Span<T> Slice(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, _length, nameof(length));
        return _array.AsSpan(start, length);
    }

    public readonly void Dispose() => arrayPool.Return(_array);

    public IEnumerator<T> GetEnumerator() => new PooledArrayEnumerator<T>(_array, _length);

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
