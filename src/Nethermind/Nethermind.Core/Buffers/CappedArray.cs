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
public readonly struct CappedArray<T> : IEquatable<CappedArray<T>> where T : struct
{
    private static readonly CappedArray<T> _null = default;
    private static readonly CappedArray<T> _empty = new([]);
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

    public static implicit operator ReadOnlySpan<T>(in CappedArray<T> array) => array.AsSpan();

    public static implicit operator CappedArray<T>(T[]? array) => array is null ? default : new CappedArray<T>(array);

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

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowArgumentOutOfRangeException() => throw new ArgumentOutOfRangeException();

    public int Length => _length;
    public int UnderlyingLength => _array?.Length ?? 0;
    public T[]? UnderlyingArray => _array;
    public bool IsUncapped => _length == _array?.Length;
    public bool IsNull => _array is null;
    public bool IsNotNull => _array is not null;
    public bool IsNullOrEmpty => _length == 0;
    public bool IsNotNullOrEmpty => _length > 0;
    public Span<T> AsSpan() => _array.AsSpan(0, _length);
    public Span<T> AsSpan(int start, int length) => _array.AsSpan(start, length);

    public T[]? ToArray()
    {
        T[]? array = _array;

        if (array is null) return null;
        if (array.Length == 0) return [];
        if (_length == array.Length) return array;
        return AsSpan().ToArray();
    }

    public override string? ToString() => typeof(T) == typeof(byte)
        ? MemoryMarshal.AsBytes(AsSpan()).ToHexString(withZeroX: true)
        : base.ToString();

    public ArraySegment<T> AsArraySegment() => AsArraySegment(0, _length);
    public ArraySegment<T> AsArraySegment(int start, int length) => new(_array!, start, length);

    /// <summary>
    /// Content-based equality: two <see cref="CappedArray{T}"/> values are equal iff their
    /// valid prefixes (<see cref="AsSpan"/>) contain the same elements in the same order.
    /// <see cref="UnderlyingArray"/> identity and oversize are not compared — that's the
    /// whole point of "capped": consumers care about the prefix, not the rental.
    /// </summary>
    public bool Equals(CappedArray<T> other)
    {
        if (_length != other._length) return false;
        if (_array is null) return other._array is null;
        if (other._array is null) return false;
        return AsSpan().SequenceEqual(other.AsSpan());
    }

    public override bool Equals(object? obj) => obj is CappedArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        // Hash by content over the valid prefix so equal content yields equal hashes
        // regardless of the underlying rental size.
        HashCode hash = default;
        hash.Add(_length);
        ReadOnlySpan<T> span = AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            hash.Add(span[i]);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(in CappedArray<T> left, in CappedArray<T> right) => left.Equals(right);
    public static bool operator !=(in CappedArray<T> left, in CappedArray<T> right) => !left.Equals(right);
}
