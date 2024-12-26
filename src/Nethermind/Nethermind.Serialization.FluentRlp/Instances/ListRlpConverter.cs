// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Serialization.FluentRlp.Instances;

public abstract class ListRlpConverter<T>
{
    public static List<T> Read(ref RlpReader reader, RefRlpReaderFunc<T> func)
    {
        return reader.ReadSequence((scoped ref RlpReader r) =>
        {
            List<T> result = [];
            while (r.HasNext)
            {
                result.Add(func(ref r));
            }

            return result;
        });
    }

    public static void Write(ref RlpWriter writer, List<T> value, RefRlpWriterAction<T> action)
    {
        writer.WriteSequence((ref RlpWriter w) =>
        {
            foreach (T v in value)
            {
                action(ref w, v);
            }
        });
    }
}

public static class ListRlpConverterExt
{
    public static List<T> ReadList<T>(this ref RlpReader reader, RefRlpReaderFunc<T> func)
        => ListRlpConverter<T>.Read(ref reader, func);

    public static void Write<T>(this ref RlpWriter writer, List<T> value, RefRlpWriterAction<T> action)
        => ListRlpConverter<T>.Write(ref writer, value, action);
}
