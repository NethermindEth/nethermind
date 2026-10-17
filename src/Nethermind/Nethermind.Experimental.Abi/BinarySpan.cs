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

    public readonly int Remaining => _span.Length - _position;

    public BinarySpanReader(ReadOnlySpan<byte> span)
    {
        _span = span;
        _position = 0;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        ReadOnlySpan<byte> result = _span.Slice(_position, count);

        _position += count;
        return result;
    }

    public ReadOnlySpan<byte> ReadBytesPadded(int length)
    {
        ReadOnlySpan<byte> bytes = ReadBytes(length);
        int padding = Math.PadTo32(length) - length;
        _position += padding;

        return bytes;
    }

    public void Advance(int bytes) => _position += bytes;

    public T Scoped<T, TCtx>(TCtx ctx, BinarySpanReaderFunc<T, TCtx> inner)
    {
        BinarySpanReader reader = new(_span[_position..]);
        T result = inner(ctx, ref reader);
        _position += reader._position;
        return result;
    }

    public T ReadOffset<T>(AbiReadFunc<T> read, ref int next)
    {
        ReadOnlySpan<byte> offsetBytes = ReadBytes(32);
        int restorePosition = _position;

        UInt256 offset = new(offsetBytes, isBigEndian: true);
        if (offset != (UInt256)next) throw new AbiException();

        _position = next;
        T result = read(ref this);
        next = _position;

        _position = restorePosition;

        return result;
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
        Take(padding).Clear();
    }

    public void Advance(int bytes) => _position += bytes;

    public Span<byte> Take(int size)
    {
        Span<byte> span = _span[_position..(_position + size)];
        _position += size;
        return span;
    }

    public void Scoped<TCtx>(TCtx ctx, BinarySpanWriterAction<TCtx> inner)
    {
        BinarySpanWriter writer = new(_span[_position..]);
        inner(ctx, ref writer);
        _position += writer._position;
    }

    public void WriteOffset<T>(AbiWriteAction<T> write, T value, ref int next)
    {
        Span<byte> offsetLocation = Take(32);
        // Constant length (rather than `..^sizeof(int)`) lets the JIT unroll the clear.
        offsetLocation[..(32 - sizeof(int))].Clear();
        BinaryPrimitives.WriteInt32BigEndian(offsetLocation[^sizeof(int)..], next);

        int restorePosition = _position;
        _position = next;
        write(ref this, value);
        next = _position;

        _position = restorePosition;
    }
}


public static class Math
{
    // TODO: Use `UInt256` when dealing with lengths
    // Equivalent to:
    // int rem = length % 32;
    // return rem == 0 ? length : length + (32 - rem);
    public static int PadTo32(int length) => (length + 31) & ~31;
}
