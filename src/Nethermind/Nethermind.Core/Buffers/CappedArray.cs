// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Buffers;

/// <summary>
/// Basically like ArraySegment, but only contain length, which reduces it size from 16byte to 12byte. Useful for
/// polling memory where memory pool usually can't return exactly the same size of data. To conserve space, The
/// underlying array can be null and this struct is meant to be non nullable, checking the `IsNull` property to check
/// if it represent null.
/// </summary>
public readonly struct CappedArray<T>
    where T : struct
{
    private readonly static CappedArray<T> _null = default;
    private readonly static CappedArray<T> _empty = new CappedArray<T>([]);
    public static ref readonly CappedArray<T> Null => ref _null;
    public static ref readonly CappedArray<T> Empty => ref _empty;
    public static object NullBoxed { get; } = _null;
    public static object EmptyBoxed { get; } = _empty;

    private readonly T[]? _array;
    private readonly int _length;

    public CappedArray(T[]? array, int length)
    {
        _array = array;
        _length = length;
    }

    public CappedArray(T[]? array)
    {
        if (array is not null)
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
        if (array is null) return default;
        return new CappedArray<T>(array);
    }

    public T this[int index]
    {
        get
        {
            T[] array = _array!;
            if (index >= _length || (uint)index >= (uint)array.Length)
            {
                ThrowArgumentOutOfRangeException();
            }

            return array[index];
        }
        set
        {
            T[] array = _array!;
            if (index >= _length || (uint)index >= (uint)array.Length)
            {
                ThrowArgumentOutOfRangeException();
            }

            array[index] = value;
        }
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowArgumentOutOfRangeException()
    {
        throw new ArgumentOutOfRangeException();
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
        T[]? array = _array;

        if (array is null) return null;
        if (array.Length == 0) return [];
        if (_length == array.Length) return array;
        return AsSpan().ToArray();
    }

    public override string? ToString()
    {
        return typeof(T) == typeof(byte) ?
            SpanExtensions.ToHexString(MemoryMarshal.AsBytes(AsSpan()), withZeroX: true) :
            base.ToString();
    }

    public readonly ArraySegment<T> AsArraySegment()
    {
        return AsArraySegment(0, _length);
    }

    public readonly ArraySegment<T> AsArraySegment(int start, int length)
    {
        return new ArraySegment<T>(_array!, start, length);
    }
}
