// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Buffers;

/// <summary>
/// Basically like ArraySegment, but only contain length, which reduces it size from 16byte to 12byte. Useful for
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

    public static implicit operator ReadOnlySpan<T>(CappedArray<T>? array)
    {
        return array.ToArrayOrNull() ?? default;
    }

    public static implicit operator CappedArray<T>?(T[]? array)
    {
        if (array == null) return null;
        return new CappedArray<T>(array);
    }

    public int Length
    {
        get => _length;
        set => _length = value;
    }

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

public static class ArrayExtensions {
    public static CappedArray<byte>? ToCappedArray(this byte[]? array)
    {
        if (array == null) return null;
        return new CappedArray<byte>(array);
    }

    public static CappedArray<byte> ToCappedArray(this Span<byte> span)
    {
        return new CappedArray<byte>(span.ToArray());
    }

    public static Span<byte> AsSpanOrEmpty(this CappedArray<byte>? array)
    {
        if (array == null) return Span<byte>.Empty;
        return array.Value.AsSpan();
    }

    public static T[]? ToArrayOrNull<T>(this CappedArray<T>? array)
    {
        if (array == null) return null;
        return array.Value.ToArray();
    }
}
