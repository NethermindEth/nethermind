// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Buffers;

/// <summary>
/// Basically like ArraySegment, but only contain length, which reduces it size from 16byte to 12byte. Useful for
/// polling memory where memory pool usually can't return exactly the same size of data. To conserve space, The
/// underlying array can be null and this struct is meant to be non nullable, checking the `IsNull` property to check
/// if it represent null.
/// </summary>
public struct CappedArray<T>
{
    private T[]? _array = null;
    private int _length = 0;

    public CappedArray(T[]? array, int length)
    {
        _array = array;
        _length = length;
    }

    public CappedArray(T[]? array)
    {
        if (array != null)
        {
            _array = array;
            _length = array.Length;
        }
    }

    public static implicit operator ReadOnlySpan<T>(CappedArray<T> array)
    {
        return array.AsSpan();
    }

    public static implicit operator CappedArray<T>(T[]? array)
    {
        if (array == null) return new CappedArray<T>(null);
        return new CappedArray<T>(array);
    }

    public int Length
    {
        get => _length;
        set => _length = value;
    }

    public T[]? Array => _array;
    public bool IsUncapped => _length == _array?.Length;
    public bool IsNull => _array is null;
    public bool IsNotNull => _array is not null;

    public Span<T> AsSpan()
    {
        return _array.AsSpan()[..Length];
    }

    public T[]? ToArray()
    {
        if (_array is null) return null;
        if (_length == _array?.Length) return _array;
        return AsSpan().ToArray();
    }
}
