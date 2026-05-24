// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

/// <summary>
/// A <see cref="ArrayPool{T}"/>-rented byte buffer paired with its valid length, so callers
/// can pass it around as a single value, write the valid portion via <see cref="Span"/>, and
/// return the rented array to the pool with <see cref="Dispose"/>. Solves the
/// over-allocation/length-tracking dance that comes with using <c>ArrayPool</c> for variable-
/// length payloads (a rented buffer is "at least N"; the consumer needs the exact length to
/// emit JSON correctly).
/// </summary>
/// <remarks>
/// <para>
/// Value-type semantics: holding a <see cref="PooledByteBuffer"/> as a field gives single
/// ownership; storing copies in a collection works only when the collection is short-lived
/// and the entries are <see cref="Dispose"/>d before the collection is dropped (a copy's
/// <see cref="Dispose"/> still returns the underlying array — the original copy is left with
/// a stale reference that must not be touched again). The streaming parity tracer uses this
/// pattern for its per-opcode push list: dispose all entries, then <c>Clear</c>.
/// </para>
/// <para>
/// Use <see cref="Rent"/> to populate from a span; <see cref="HasValue"/> to test presence;
/// <see cref="Span"/> for read access; <see cref="Dispose"/> to release. <see cref="Length"/>
/// reports the valid prefix even after the array has been returned (which is fine as long as
/// callers respect the dispose-then-don't-touch contract).
/// </para>
/// </remarks>
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
