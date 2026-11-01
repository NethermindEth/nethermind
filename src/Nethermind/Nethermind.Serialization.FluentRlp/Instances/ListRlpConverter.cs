// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Serialization.FluentRlp.Instances;

/// <summary>Converts a <see cref="List{T}"/> as a sequence of its items.</summary>
public abstract class ListRlpConverter<T>
{
    /// <summary>Reads a sequence and converts each item with <paramref name="func"/>.</summary>
    public static List<T> Read(ref RlpReader reader, RefRlpReaderFunc<T> func)
        => reader.ReadSequence(func, static (scoped ref RlpReader r, RefRlpReaderFunc<T> func) =>
        {
            List<T> result = new(r.CountItems());
            while (r.HasNext)
            {
                result.Add(func(ref r));
            }

            return result;
        });

    /// <summary>Writes a sequence and converts each item with <paramref name="action"/>.</summary>
    public static void Write(ref RlpWriter writer, List<T> value, RefRlpWriterAction<T> action)
    {
        (List<T>, RefRlpWriterAction<T>) ctx = ValueTuple.Create(value, action);
        writer.WriteSequence(ctx, static (ref RlpWriter w, (List<T>, RefRlpWriterAction<T>) ctx) =>
        {
            (List<T> value, RefRlpWriterAction<T> action) = ctx;
            foreach (T v in value)
            {
                action(ref w, v);
            }
        });
    }
}

/// <summary>Extension methods for <see cref="ListRlpConverter{T}"/>.</summary>
public static class ListRlpConverterExt
{
    /// <inheritdoc cref="ListRlpConverter{T}.Read"/>
    public static List<T> ReadList<T>(this ref RlpReader reader, RefRlpReaderFunc<T> func)
        => ListRlpConverter<T>.Read(ref reader, func);

    /// <inheritdoc cref="ListRlpConverter{T}.Write"/>
    public static void Write<T>(this ref RlpWriter writer, List<T> value, RefRlpWriterAction<T> action)
        => ListRlpConverter<T>.Write(ref writer, value, action);
}
