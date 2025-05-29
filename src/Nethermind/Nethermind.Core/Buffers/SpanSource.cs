// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Buffers;

/// <summary>
/// Represents a source of a span.
/// It can be either an array or another span source.
/// </summary>
public readonly struct SpanSource : ISpanSource
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
}
