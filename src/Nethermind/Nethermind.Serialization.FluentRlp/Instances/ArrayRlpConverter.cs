// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.FluentRlp.Instances;

/// <summary>Converts an array as a sequence of its items.</summary>
public abstract class ArrayRlpConverter<T>
{
    /// <summary>Reads a sequence and converts each item with <paramref name="func"/>.</summary>
    public static T[] Read(ref RlpReader reader, RefRlpReaderFunc<T> func)
        => reader.ReadSequence(func, static (scoped ref RlpReader r, RefRlpReaderFunc<T> func) =>
        {
            T[] result = new T[r.CountItems()];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = func(ref r);
            }

            return result;
        });

    /// <summary>Writes a sequence and converts each item with <paramref name="action"/>.</summary>
    public static void Write(ref RlpWriter writer, T[] value, RefRlpWriterAction<T> action)
    {
        (T[], RefRlpWriterAction<T>) ctx = ValueTuple.Create(value, action);
        writer.WriteSequence(ctx, static (ref RlpWriter w, (T[], RefRlpWriterAction<T>) ctx) =>
        {
            (T[] value, RefRlpWriterAction<T> action) = ctx;
            foreach (T v in value)
            {
                action(ref w, v);
            }
        });
    }
}

/// <summary>Extension methods for <see cref="ArrayRlpConverter{T}"/>.</summary>
public static class ArrayRlpConverterExt
{
    /// <inheritdoc cref="ArrayRlpConverter{T}.Read"/>
    public static T[] ReadArray<T>(this ref RlpReader reader, RefRlpReaderFunc<T> func)
        => ArrayRlpConverter<T>.Read(ref reader, func);

    /// <inheritdoc cref="ArrayRlpConverter{T}.Write"/>
    public static void Write<T>(this ref RlpWriter writer, T[] value, RefRlpWriterAction<T> action)
        => ArrayRlpConverter<T>.Write(ref writer, value, action);
}
