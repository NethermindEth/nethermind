// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Serialization.FluentRlp;

/// <summary>Entry points to encode and decode a whole RLP item.</summary>
public static class Rlp
{
    /// <summary>Encodes the items written by <paramref name="action"/>.</summary>
    /// <returns>A new array of the exact encoded length.</returns>
    /// <remarks><paramref name="action"/> runs once.</remarks>
    public static byte[] Write(RefRlpWriterAction action)
        => Write(action, static (ref RlpWriter w, RefRlpWriterAction action) => action(ref w));

    /// <inheritdoc cref="Write(RefRlpWriterAction)"/>
    public static byte[] Write<TContext>(TContext ctx, RefRlpWriterAction<TContext> action)
        where TContext : allows ref struct
    {
        RlpWriter writer = RlpWriter.BufferingWriter();
        action(ref writer, ctx);

        byte[] result = new byte[writer.Length];
        writer.CopyBufferedTo(result);
        writer.ReturnBuffers();
        return result;
    }

    /// <summary>Encodes the items written by <paramref name="action"/> to <paramref name="output"/>.</summary>
    /// <returns>The number of bytes written.</returns>
    /// <remarks><paramref name="action"/> runs once.</remarks>
    public static int Write<TContext>(IBufferWriter<byte> output, TContext ctx, RefRlpWriterAction<TContext> action)
        where TContext : allows ref struct
    {
        RlpWriter writer = RlpWriter.BufferingWriter();
        action(ref writer, ctx);

        int length = writer.Length;
        writer.CopyBufferedTo(output.GetSpan(length)[..length]);
        writer.ReturnBuffers();
        output.Advance(length);
        return length;
    }

    /// <summary>Decodes <paramref name="source"/> with <paramref name="func"/>, which must consume all of it.</summary>
    /// <exception cref="RlpReaderException">The input is malformed or has trailing bytes.</exception>
    public static T Read<T>(ReadOnlySpan<byte> source, RefRlpReaderFunc<T> func)
        where T : allows ref struct
    {
        RlpReader reader = new(source);
        T result = func(ref reader);
        if (reader.HasNext) throw new RlpReaderException("RLP has trailing bytes");
        return result;
    }
}
