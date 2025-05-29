// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

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
    private readonly object _obj;

    public SpanSource(byte[] array)
    {
        _obj = array;
    }

    public SpanSource(ISpanSource source)
    {
        _obj = source;
    }

    // TODO: make it correct
    public int MemorySize => 0;

    public int Length
    {
        get
        {
            var obj = _obj;
            if (obj is byte[] array)
                return array.Length;

            return Unsafe.As<ISpanSource>(obj).Length;
        }
    }
    public bool SequenceEqual(ReadOnlySpan<byte> other)
    {
        var obj = _obj;
        if (obj is byte[] array)
            return array.AsSpan().SequenceEqual(other);

        return Unsafe.As<ISpanSource>(obj).SequenceEqual(other);
    }

    public int CommonPrefixLength(ReadOnlySpan<byte> other)
    {
        var obj = _obj;
        if (obj is byte[] array)
            return array.AsSpan().CommonPrefixLength(other);

        return Unsafe.As<ISpanSource>(obj).CommonPrefixLength(other);
    }

    public Span<byte> Span
    {
        get
        {
            var obj = _obj;
            if (obj is byte[] array)
                return array.AsSpan();

            return Unsafe.As<ISpanSource>(obj).Span;
        }
    }

    public bool IsNotNull => !IsNull;
    public bool IsNull => _obj == null;
    public bool IsNotNullOrEmpty => throw new Exception("");

    public static readonly SpanSource Empty = new([]);

    public static readonly SpanSource Null = default;

    public bool Equals(SpanSource other)
    {
        throw new NotImplementedException();
    }

    public byte[] ToArray()
    {
        throw new NotImplementedException();
    }
}
