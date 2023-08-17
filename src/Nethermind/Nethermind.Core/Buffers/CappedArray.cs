// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Buffers;

/// <summary>
/// Basically like ArraySegment, but only contain length, which reduces it size from 24byte to 16byte. Useful for
/// polling memory where memory pool usually can't return exactly the same size of data
/// </summary>
public struct CappedArray<T>
{
    private T[] _array;
    private int _length;

    public CappedArray(T[] array, int length)
    {
        _array = array;
        _length = length;
    }

    public CappedArray(T[] array):this(array, array.Length)
    {
    }

    public static implicit operator CappedArray<T>(T[] array)
    {
        return new CappedArray<T>(array);
    }

    public int Length => _length;
    public T[] Array => _array;

    public Span<T> AsSpan()
    {
        return _array.AsSpan()[..Length];
    }

    public T[] ToArray()
    {
        if (_length == _array.Length) return _array;
        return AsSpan().ToArray();
    }
}
