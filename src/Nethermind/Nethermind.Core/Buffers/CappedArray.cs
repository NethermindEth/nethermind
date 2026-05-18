// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Buffers;

/// <summary>
/// Like ArraySegment but with explicit offset+length, supporting zero-copy logical slices over a shared
/// backing array. Useful for pooled memory where the pool may return a larger buffer than requested. The
/// underlying array can be null; check the <see cref="IsNull"/> property to detect that case.
/// </summary>
public readonly struct CappedArray<T> where T : struct
{
    private static readonly CappedArray<T> _null = default;
    private static readonly CappedArray<T> _empty = new([]);
    public static ref readonly CappedArray<T> Null => ref _null;
    public static ref readonly CappedArray<T> Empty => ref _empty;
    public static object NullBoxed { get; } = _null;
    public static object EmptyBoxed { get; } = _empty;

    private readonly T[]? _array;
    private readonly int _offset;
    private readonly int _length;

    public CappedArray(T[]? array, int length)
        : this(array, 0, length)
    {
    }

    public CappedArray(T[]? array, int offset, int length)
    {
        _array = array;
        _offset = offset;
        _length = length;
    }

    public CappedArray(T[]? array)
    {
        if (array is not null)
        {
            _array = array;
            _offset = 0;
            _length = array.Length;
        }
        else
        {
            _array = null;
            _offset = 0;
            _length = 0;
        }
    }

    public static implicit operator ReadOnlySpan<T>(in CappedArray<T> array) => array.AsSpan();

    public static implicit operator CappedArray<T>(T[]? array) => array is null ? default : new CappedArray<T>(array);

    public T this[int index]
    {
        get
        {
            // Validate against the logical slice first - a negative `index` would otherwise wrap into
            // `_offset + index` and silently read bytes from before the slice (e.g. parent RLP for an
            // inline trie child). The underlying-array check defends against an oversized _length passed
            // to the (array, offset, length) constructor.
            if ((uint)index >= (uint)_length)
            {
                ThrowArgumentOutOfRangeException();
            }

            T[] array = _array!;
            int arrayIndex = _offset + index;
            if ((uint)arrayIndex >= (uint)array.Length)
            {
                ThrowArgumentOutOfRangeException();
            }

            return array[arrayIndex];
        }
        set
        {
            if ((uint)index >= (uint)_length)
            {
                ThrowArgumentOutOfRangeException();
            }

            T[] array = _array!;
            int arrayIndex = _offset + index;
            if ((uint)arrayIndex >= (uint)array.Length)
            {
                ThrowArgumentOutOfRangeException();
            }

            array[arrayIndex] = value;
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowArgumentOutOfRangeException() => throw new ArgumentOutOfRangeException();

    public int Length => _length;
    public int Offset => _offset;
    public int UnderlyingLength => _array?.Length ?? 0;
    public T[]? UnderlyingArray => _array;
    public bool IsUncapped => _offset == 0 && _length == _array?.Length;
    public bool IsNull => _array is null;
    public bool IsNotNull => _array is not null;
    public bool IsNullOrEmpty => _length == 0;
    public bool IsNotNullOrEmpty => _length > 0;
    public Span<T> AsSpan() => _array.AsSpan(_offset, _length);
    public Span<T> AsSpan(int start, int length)
    {
        // Validate against the logical slice first; otherwise a negative `start` would wrap into
        // `_offset + start` and yield a span over bytes outside the slice (e.g. parent RLP).
        if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start))
        {
            ThrowArgumentOutOfRangeException();
        }

        return _array.AsSpan(_offset + start, length);
    }

    public T[]? ToArray()
    {
        T[]? array = _array;

        if (array is null) return null;
        if (array.Length == 0) return [];
        if (_offset == 0 && _length == array.Length) return array;
        return AsSpan().ToArray();
    }

    public override string? ToString() => typeof(T) == typeof(byte)
        ? MemoryMarshal.AsBytes(AsSpan()).ToHexString(withZeroX: true)
        : base.ToString();

    public ArraySegment<T> AsArraySegment() => AsArraySegment(0, _length);
    public ArraySegment<T> AsArraySegment(int start, int length)
    {
        // See AsSpan(start, length) - validate against the logical slice before adding _offset.
        if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start))
        {
            ThrowArgumentOutOfRangeException();
        }

        return new(_array!, _offset + start, length);
    }
}
