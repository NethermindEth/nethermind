// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.Hsst;

public interface IByteBufferWriter
{
    Span<byte> GetSpan(int sizeHint = 0);
    void Advance(int count);
    long Written { get; }

    /// <summary>
    /// Smallest writer-local offset (in the same coordinate system as
    /// <see cref="Written"/>) that maps to a 4 KiB-aligned byte in the writer's
    /// eventual destination. Callers can pad to the next 4 KiB boundary with
    /// <c>(-(Written - FirstOffset)) &amp; 4095L</c>. For writers whose backing
    /// destination has no inherent alignment (e.g. transient in-memory buffers),
    /// implementations may return <c>0</c>.
    /// </summary>
    long FirstOffset { get; }

    static void Copy<TWriter>(ref TWriter writer, ReadOnlySpan<byte> value) where TWriter : IByteBufferWriter
    {
        while (value.Length > 0)
        {
            int chunk = Math.Min(value.Length, 256);
            value[..chunk].CopyTo(writer.GetSpan(chunk));
            writer.Advance(chunk);
            value = value[chunk..];
        }
    }
}

/// <summary>
/// Writers that can produce a reader over their already-written bytes. The reader
/// covers <c>[Written − pastSize, Written)</c> at the call site (offset 0 of the reader
/// equals byte <c>(Written − pastSize)</c> of the writer). Reader length is fixed at
/// <c>pastSize</c>; subsequent writes do not extend the reader's window.
/// Implementations whose backing buffer can be relocated by later <c>GetSpan</c>
/// calls (e.g. <see cref="PooledByteBufferWriter.Writer"/>) must return a reader
/// that re-resolves the buffer pointer per access.
///
/// Only one reader is allowed at a time per writer. The reader is a borrow over
/// writer-owned state (and may be a freely-copyable ref struct), so the writer
/// holds the underlying resource and there is no per-reader Dispose. Implementations
/// that own an OS resource for the read window (e.g. an mmap view) must therefore
/// reject a second <see cref="OpenReader"/> while a prior view is still active —
/// the caller must finish using the previous reader before opening another, and
/// the writer releases the view on its own <c>Dispose</c>.
/// </summary>
public interface IByteBufferWriterWithReader<TReader, TPin> : IByteBufferWriter
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TPin : struct, IBufferPin, allows ref struct
{
    [UnscopedRef]
    TReader OpenReader(long pastSize);

    /// <summary>
    /// Release the view opened by the most recent <see cref="OpenReader"/> call.
    /// Implementations that hold no per-reader resource may treat this as a no-op.
    /// Callers must invoke this once they are done with the reader so the writer
    /// can re-open another (the single-reader-at-a-time contract above) and so
    /// any underlying OS resource is released eagerly rather than at writer dispose.
    /// </summary>
    void DisposeActiveReader();
}

public unsafe struct SpanBufferWriter(Span<byte> buffer, long firstOffset = 0) : IByteBufferWriterWithReader<SpanByteReader, NoOpPin>
{
    private readonly byte* _buffer = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(buffer));
    private readonly int _length = buffer.Length;
    private readonly long _firstOffset = firstOffset;
    private int _written;

    public readonly Span<byte> GetSpan(int sizeHint = 0) => new(_buffer + _written, _length - _written);
    public void Advance(int count) => _written += count;
    public readonly long Written => _written;
    public readonly long FirstOffset => _firstOffset;

    public readonly SpanByteReader OpenReader(long pastSize)
        => new(new ReadOnlySpan<byte>(_buffer + (_written - pastSize), checked((int)pastSize)));

    public readonly void DisposeActiveReader() { }
}
