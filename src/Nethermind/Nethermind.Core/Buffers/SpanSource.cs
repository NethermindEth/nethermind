// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;


namespace Nethermind.Core.Buffers;

/// <summary>
/// Represents a source of a span.
/// </summary>
/// <remarks>
/// The design is similar to the <see cref="ValueTask"/> where a single field contains
/// a value of two types. In this case it can be an array, or an actual implementation of a <see cref="ISpanSource"/>,
/// like <see cref="TinyArray"/>.
/// </remarks>
public readonly struct SpanSource : ISpanSource, IEquatable<SpanSource>
{
    /// <summary>
    /// A union reference, discriminated by its type.
    /// It can be either byte[], or an actual implementation of a <see cref="ISpanSource"/>.
    /// </summary>
    private readonly object _obj;

    public SpanSource(byte[] array)
    {
        _obj = array;
    }

    public SpanSource(CappedArray<byte> capped)
    {
        _obj = new CappedArraySource(capped);
    }

    public static implicit operator SpanSource(byte[] bytes) => new(bytes);

    public int MemorySize
    {
        get
        {
            const int objSize = MemorySizes.RefSize;

            var obj = _obj;

            if (obj == null)
                return objSize;

            if (obj is byte[] array)
            {
                return ReferenceEquals(array, Empty._obj)
                    ? objSize
                    : objSize + MemorySizes.ArrayOverhead + array.Length;
            }

            return obj is CappedArraySource capped ? objSize + capped.MemorySize : 0;
        }
    }

    public int Length
    {
        get
        {
            var obj = _obj;
            if (obj is byte[] array)
                return array.Length;

            if (obj is null) return 0;
            return Unsafe.As<CappedArraySource>(obj).Length;
        }
    }

    public Span<byte> Span
    {
        get
        {
            var obj = _obj;

            if (obj is null)
                return Span<byte>.Empty;

            if (obj is byte[] array)
                return array.AsSpan();

            if (obj is CappedArraySource capped)
                return capped.Span;

            return Span<byte>.Empty;
            //return Unsafe.As<CappedArraySource>(obj).Span;
        }
    }

    public bool IsNotNull => !IsNull;
    public bool IsNull => _obj == null;
    public bool IsNullOrEmpty
    {
        get
        {
            var obj = _obj;

            if (obj == null)
                return true;

            if (obj is byte[] array)
                return array.Length == 0;

            return Unsafe.As<CappedArraySource>(obj).Length == 0;
        }
    }

    public bool IsNotNullOrEmpty
    {
        get
        {
            var obj = _obj;

            if (obj == null)
                return false;

            if (obj is byte[] array)
                return array.Length != 0;

            return Unsafe.As<CappedArraySource>(obj).Length != 0;
        }
    }

    public static readonly SpanSource Empty = new([]);

    public static readonly SpanSource Null = default;

    public bool Equals(SpanSource other)
    {
        Span<byte> comparand = other.Span;

        var obj = _obj;
        if (obj is byte[] array)
        {
            return array.AsSpan().SequenceEqual(comparand);
        }

        return Unsafe.As<CappedArraySource>(obj).SequenceEqual(comparand);
    }

    /// <summary>
    /// A <see cref="IsNull"/> aware span source. Returns null if the underlying is null or materializes the array.
    /// </summary>
    /// <returns></returns>
    public byte[]? ToArray()
    {
        var obj = _obj;
        if (obj is null)
            return null;

        if (obj is byte[] array)
        {
            return array;
        }

        return Unsafe.As<CappedArraySource>(obj).Span.ToArray();
    }

    public bool TryGetCappedArray(out CappedArray<byte> cappedArray)
    {
        if (_obj is CappedArraySource source)
        {
            cappedArray = source.Capped;
            return true;
        }

        cappedArray = default;
        return false;
    }

    private sealed class CappedArraySource : ISpanSource
    {
        public readonly CappedArray<byte> Capped;

        public CappedArraySource(CappedArray<byte> capped)
        {
            Capped = capped;
        }

        public int Length => Capped.Length;

        public bool SequenceEqual(ReadOnlySpan<byte> other) => Capped.AsSpan().SequenceEqual(other);

        public Span<byte> Span => Capped.AsSpan();
        public int MemorySize => MemorySizes.SmallObjectOverhead +
                                 MemorySizes.ArrayOverhead +
                                 Capped.UnderlyingLength;
    }

    public override string ToString()
    {
        var obj = _obj;
        if (obj is null)
            return "null";

        if (obj is byte[] array)
        {
            return $"array: {array.ToHexString()}";
        }

        return $"capped: {Unsafe.As<CappedArraySource>(obj).Span.ToHexString()}";
    }
}
