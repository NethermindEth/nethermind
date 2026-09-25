// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;

namespace Nethermind.Serialization.FluentRlp;

public static class Rlp
{
    public static byte[] Write(RefRlpWriterAction action)
        => Write(action, static (ref RlpWriter w, RefRlpWriterAction action) => action(ref w));

    public static byte[] Write<TContext>(TContext ctx, RefRlpWriterAction<TContext> action)
        where TContext : allows ref struct
    {
        RlpWriter lengthWriter = RlpWriter.LengthWriter();
        action(ref lengthWriter, ctx);
        FixedArrayBufferWriter<byte> bufferWriter = new(lengthWriter.Length);
        RlpWriter contentWriter = RlpWriter.ContentWriter(bufferWriter);
        action(ref contentWriter, ctx);

        return bufferWriter.Buffer;
    }

    public static T Read<T>(ReadOnlySpan<byte> source, RefRlpReaderFunc<T> func)
        where T : allows ref struct
    {
        RlpReader reader = new(source);
        T result = func(ref reader);
        if (reader.HasNext) throw new RlpReaderException("RLP has trailing bytes");
        return result;
    }
}

/// <remarks>
/// The existing <see cref="ArrayBufferWriter{T}"/> performs various bound checks and supports resizing buffers
/// which we don't need for our use case.
/// </remarks>
/// <param name="capacity">The capacity of the underlying buffer.</param>
internal class FixedArrayBufferWriter<T>(int capacity) : IBufferWriter<T>
{
    private readonly T[] _buffer = new T[capacity];
    private int _index;

    public T[] Buffer => _buffer;

    public void Advance(int count) => _index += count;

    public Memory<T> GetMemory(int sizeHint = 0)
    {
        Debug.Assert(_buffer.Length >= _index);
        return _buffer.AsMemory(_index);
    }

    public Span<T> GetSpan(int sizeHint = 0)
    {
        Debug.Assert(_buffer.Length >= _index);
        return _buffer.AsSpan(_index);
    }
}
