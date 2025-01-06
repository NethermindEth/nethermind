// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.FluentRlp;

public static class Rlp
{
    public static ReadOnlyMemory<byte> Write(RefRlpWriterAction action)
        => Write(action, static (ref RlpWriter w, RefRlpWriterAction action) => action(ref w));

    public static ReadOnlyMemory<byte> Write<TContext>(TContext ctx, RefRlpWriterAction<TContext> action)
        where TContext : allows ref struct
    {
        var lengthWriter = RlpWriter.LengthWriter();
        action(ref lengthWriter, ctx);
        var buffer = new ArrayBufferWriter<byte>(lengthWriter.Length);
        var contentWriter = RlpWriter.ContentWriter(buffer);
        action(ref contentWriter, ctx);

        return buffer.WrittenMemory;
    }

    public static T Read<T>(ReadOnlyMemory<byte> source, RefRlpReaderFunc<T> func) where T : allows ref struct
        => Read(source.Span, func);

    [OverloadResolutionPriority(1)]
    public static T Read<T>(ReadOnlySpan<byte> source, RefRlpReaderFunc<T> func)
        where T : allows ref struct
    {
        var reader = new RlpReader(source);
        T result = func(ref reader);
        if (reader.HasNext) throw new RlpReaderException("RLP has trailing bytes");
        return result;
    }
}
