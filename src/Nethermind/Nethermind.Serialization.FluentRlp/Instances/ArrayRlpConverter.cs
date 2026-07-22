// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Serialization.FluentRlp.Instances;

public abstract class ArrayRlpConverter<T>
{
    public static T[] Read(ref RlpReader reader, RefRlpReaderFunc<T> func)
    {
        return reader.ReadSequence(func, static (scoped ref RlpReader r, RefRlpReaderFunc<T> func) =>
        {
            List<T> result = [];
            while (r.HasNext)
            {
                result.Add(func(ref r));
            }

            // TODO: Avoid copying
            return result.ToArray();
        });
    }

    public static void Write(ref RlpWriter writer, T[] value, RefRlpWriterAction<T> action)
    {
        var ctx = ValueTuple.Create(value, action);
        writer.WriteSequence(ctx, static (ref RlpWriter w, (T[], RefRlpWriterAction<T>) ctx) =>
        {
            var (value, action) = ctx;
            foreach (T v in value)
            {
                action(ref w, v);
            }
        });
    }
}

public static class ArrayRlpConverterExt
{
    public static T[] ReadArray<T>(this ref RlpReader reader, RefRlpReaderFunc<T> func)
        => ArrayRlpConverter<T>.Read(ref reader, func);

    public static void Write<T>(this ref RlpWriter writer, T[] value, RefRlpWriterAction<T> action)
        => ArrayRlpConverter<T>.Write(ref writer, value, action);
}
