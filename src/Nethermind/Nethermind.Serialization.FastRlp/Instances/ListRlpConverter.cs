// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Serialization.FastRlp.Instances;

// TODO: We might want to introduce an interface for collection types (ex. List)
// Main issue are HKT (aka Generics of Generics)
public abstract class ListRlpConverter<T>
{
    public static List<T> Read<TConverter>(ref RlpReader reader) where TConverter : IRlpConverter<T>
    {
        return reader.ReadSequence((scoped ref RlpReader r) =>
        {
            List<T> result = [];
            while (r.HasNext)
            {
                result.Add(TConverter.Read(ref r));
            }

            return result;
        });
    }

    public static void Write<TConverter>(ref RlpWriter writer, List<T> value) where TConverter : IRlpConverter<T>
    {
        writer.WriteSequence((ref RlpWriter w) =>
        {
            foreach (T v in value)
            {
                TConverter.Write(ref w, v);
            }
        });
    }
}

public static class ListRlpConverterExt
{
    public static List<T> ReadList<T, TConverter>(this ref RlpReader reader)
        where TConverter : IRlpConverter<T>
        => ListRlpConverter<T>.Read<TConverter>(ref reader);

    public static void Write<T, TConverter>(this ref RlpWriter writer, List<T> value)
        where TConverter : IRlpConverter<T>
        => ListRlpConverter<T>.Write<TConverter>(ref writer, value);
}
