// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Int256;

namespace Nethermind.Experimental.Abi;

public delegate T BinarySpanReaderFunc<out T, in TCtx>(TCtx ctx, ref BinarySpanReader r);

public ref struct BinarySpanReader
{
    private readonly ReadOnlySpan<byte> _span;
    private int _position;

    public BinarySpanReader(ReadOnlySpan<byte> span)
    {
        _span = span;
        _position = 0;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        var result = _span.Slice(_position, count);

        _position += count;
        return result;
    }

    public ReadOnlySpan<byte> ReadBytesPadded(int length)
    {
        ReadOnlySpan<byte> bytes = ReadBytes(length);
        var padding = Math.PadTo32(length) - length;
        _position += padding;

        return bytes;
    }

    public void Advance(int bytes)
    {
        _position += bytes;
    }

    public T Scoped<T, TCtx>(TCtx ctx, BinarySpanReaderFunc<T, TCtx> inner)
    {
        var reader = new BinarySpanReader(_span[_position..]);
        T result = inner(ctx, ref reader);
        _position += reader._position;
        return result;
    }

    public (T, int) ReadOffset<T, TCtx>(TCtx ctx, BinarySpanReaderFunc<T, TCtx> inner)
    {
        var offsetBytes = ReadBytes(32);
        var restorePosition = _position;

        var offset = new UInt256(offsetBytes, isBigEndian: true);

        _position = (int)offset;

        var valueStart = _position;
        T result = inner(ctx, ref this);
        var valueEnd = _position;
        var read = valueEnd - valueStart;

        _position = restorePosition;

        return (result, read);
    }
}

public delegate void BinarySpanWriterAction<in TCtx>(TCtx ctx, ref BinarySpanWriter w);

public ref struct BinarySpanWriter
{
    private readonly Span<byte> _span;
    private int _position;

    public readonly int Written => _position;

    public BinarySpanWriter(Span<byte> span)
    {
        _span = span;
        _position = 0;
    }

    public void Write(scoped ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_span[_position..]);
        _position += bytes.Length;
    }

    public void WritePadded(scoped ReadOnlySpan<byte> bytes)
    {
        Write(bytes);

        int padding = Math.PadTo32(bytes.Length) - bytes.Length;
        _position += padding;
    }

    public int Advance(int bytes)
    {
        var startPosition = _position;
        _position += bytes;
        return startPosition;
    }

    public Span<byte> Take(int size)
    {
        var span = _span[_position..(_position + size)];
        _position += size;
        return span;
    }

    public void Scoped<TCtx>(TCtx ctx, BinarySpanWriterAction<TCtx> inner)
    {
        var writer = new BinarySpanWriter(_span[_position..]);
        inner(ctx, ref writer);
        _position += writer._position;
    }

    public void WriteOffset<TCtx>(int offset, in TCtx ctx, BinarySpanWriterAction<TCtx> inner)
    {
        Span<byte> offsetLocation = _span[offset..(offset + 32)];
        BinaryPrimitives.WriteInt32BigEndian(offsetLocation[^sizeof(int)..], _position);

        inner(ctx, ref this);
    }
}


public static class Math
{
    // TODO: Use `UInt256` when dealing with lengths
    public static int PadTo32(int length)
    {
        int rem = length % 32;
        return rem == 0 ? length : length + (32 - rem);
    }
}
