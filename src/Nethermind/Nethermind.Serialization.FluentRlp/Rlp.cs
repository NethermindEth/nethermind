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
        var lengthWriter = RlpWriter.LengthWriter();
        action(ref lengthWriter, ctx);
        var bufferWriter = new FixedArrayBufferWriter<byte>(lengthWriter.Length);
        var contentWriter = RlpWriter.ContentWriter(bufferWriter);
        action(ref contentWriter, ctx);

        return bufferWriter.Buffer;
    }

    public static T Read<T>(ReadOnlySpan<byte> source, RefRlpReaderFunc<T> func)
        where T : allows ref struct
    {
        var reader = new RlpReader(source);
        T result = func(ref reader);
        if (reader.HasNext) throw new RlpReaderException("RLP has trailing bytes");
        return result;
    }
}

/// <remarks>
/// The existing <see cref="ArrayBufferWriter{T}"/> performs various bound checks and supports resizing buffers
/// which we don't need for our use case.
/// </remarks>
internal class FixedArrayBufferWriter<T> : IBufferWriter<T>
{
    private readonly T[] _buffer;
    private int _index;

    public T[] Buffer => _buffer;

    /// <summary>
    /// Creates an instance of an <see cref="FixedArrayBufferWriter{T}"/>, in which data can be written to,
    /// with a fixed capacity specified.
    /// </summary>
    /// <param name="capacity">The capacity of the underlying buffer.</param>
    public FixedArrayBufferWriter(int capacity)
    {
        _buffer = new T[capacity];
        _index = 0;
    }

    public void Advance(int count)
    {
        _index += count;
    }

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
