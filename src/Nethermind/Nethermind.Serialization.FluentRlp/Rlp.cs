// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

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
        var serialized = new byte[lengthWriter.Length];
        var contentWriter = RlpWriter.ContentWriter(serialized);
        action(ref contentWriter, ctx);

        return serialized;
    }

    public static T Read<T>(ReadOnlySpan<byte> source, RefRlpReaderFunc<T> func)
        where T : allows ref struct
    {
        var reader = new RlpReader(source);
        T result = func(ref reader);
        // TODO: We might want to add an option to check for no trailing bytes.
        return result;
    }

    public static void Read(ReadOnlySpan<byte> source, RefRlpReaderAction func)
    {
        var reader = new RlpReader(source);
        func(ref reader);
    }
}
