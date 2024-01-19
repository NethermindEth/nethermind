// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Buffers;

/// <summary>
/// Basically like ArraySegment, but only contain length, which reduces it size from 16byte to 12byte. Useful for
/// polling memory where memory pool usually can't return exactly the same size of data. To conserve space, The
/// underlying array can be null and this struct is meant to be non nullable, checking the `IsNull` property to check
/// if it represent null.
/// </summary>
public readonly struct CappedArray<T>
{
    private readonly static CappedArray<T> _null = default;
    private readonly static CappedArray<T> _empty = new CappedArray<T>(Array.Empty<T>());
    public static ref readonly CappedArray<T> Null => ref _null;
    public static ref readonly CappedArray<T> Empty => ref _empty;

    private readonly T[]? _array;
    private readonly int _length;

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

    public static implicit operator ReadOnlySpan<T>(in CappedArray<T> array)
    {
        return array.AsSpan();
    }

    public static implicit operator CappedArray<T>(T[]? array)
    {
        if (array == null) return default;
        return new CappedArray<T>(array);
    }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _length);
            return _array![index];
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _length);
            _array![index] = value;
        }
    }

    public readonly int Length => _length;
    public readonly int UnderlyingLength => _array?.Length ?? 0;

    public readonly T[]? UnderlyingArray => _array;
    public readonly bool IsUncapped => _length == _array?.Length;
    public readonly bool IsNull => _array is null;
    public readonly bool IsNotNull => _array is not null;
    public readonly bool IsNotNullOrEmpty => _length > 0;

    public readonly Span<T> AsSpan()
    {
        return _array.AsSpan(0, _length);
    }

    public readonly Span<T> AsSpan(int start, int length)
    {
        return _array.AsSpan(start, length);
    }

    public readonly T[]? ToArray()
    {
        if (_array is null) return null;
        if (_length == _array?.Length) return _array;
        return AsSpan().ToArray();
    }
}
