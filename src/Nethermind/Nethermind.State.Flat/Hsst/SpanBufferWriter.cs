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
/// </summary>
public interface IByteBufferWriterWithReader<TReader, TPin> : IByteBufferWriter
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TPin : struct, IBufferPin, allows ref struct
{
    [UnscopedRef]
    TReader OpenReader(long pastSize);
}

public unsafe struct SpanBufferWriter(Span<byte> buffer) : IByteBufferWriterWithReader<SpanByteReader, NoOpPin>
{
    private readonly byte* _buffer = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(buffer));
    private readonly int _length = buffer.Length;
    private int _written;

    public readonly Span<byte> GetSpan(int sizeHint = 0) => new(_buffer + _written, _length - _written);
    public void Advance(int count) => _written += count;
    public readonly long Written => _written;

    public readonly SpanByteReader OpenReader(long pastSize)
        => new(new ReadOnlySpan<byte>(_buffer + (_written - pastSize), checked((int)pastSize)));
}
