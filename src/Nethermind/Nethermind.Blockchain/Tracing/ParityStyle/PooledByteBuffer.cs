// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

/// <summary>
/// <see cref="ArrayPool{T}"/>-rented byte buffer paired with its valid length.
/// Holds single ownership when used as a field; copies stored in a collection must be
/// disposed before the collection is cleared (a copy's Dispose still returns the array,
/// invalidating any sibling reference).
/// </summary>
internal struct PooledByteBuffer : IDisposable
{
    private byte[]? _buffer;
    private int _length;

    private PooledByteBuffer(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
    }

    public static PooledByteBuffer Rent(ReadOnlySpan<byte> source)
    {
        if (source.Length == 0) return default;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(source.Length);
        source.CopyTo(buffer);
        return new PooledByteBuffer(buffer, source.Length);
    }

    public readonly bool HasValue => _buffer is not null;

    public readonly int Length => _length;

    public readonly ReadOnlySpan<byte> Span =>
        _buffer is null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(_buffer, 0, _length);

    public void Dispose()
    {
        if (_buffer is null) return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null;
        _length = 0;
    }
}
