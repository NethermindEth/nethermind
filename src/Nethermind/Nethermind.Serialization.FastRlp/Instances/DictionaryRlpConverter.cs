// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Serialization.FastRlp.Instances;

public abstract class DictionaryRlpConverter<TKey, TValue> where TKey : notnull
{
    public static Dictionary<TKey, TValue> Read<TKeyConverter, TValueConverter>(ref RlpReader reader)
        where TKeyConverter : IRlpConverter<TKey>
        where TValueConverter : IRlpConverter<TValue>
    {
        return reader.ReadSequence((scoped ref RlpReader r) =>
        {
            Dictionary<TKey, TValue> result = [];
            while (r.HasNext)
            {
                (TKey key, TValue value) = r.ReadSequence((scoped ref RlpReader r) =>
                {
                    TKey key = TKeyConverter.Read(ref r);
                    TValue value = TValueConverter.Read(ref r);

                    return (key, value);
                });

                result.Add(key, value);
            }

            return result;
        });
    }

    public static void Write<TKeyConverter, TValueConverter>(ref RlpWriter writer, Dictionary<TKey, TValue> value)
        where TKeyConverter : IRlpConverter<TKey>
        where TValueConverter : IRlpConverter<TValue>
    {
        writer.WriteSequence((ref RlpWriter w) =>
        {
            foreach ((TKey k, TValue v) in value)
            {
                w.WriteSequence((ref RlpWriter w) =>
                {
                    TKeyConverter.Write(ref w, k);
                    TValueConverter.Write(ref w, v);
                });
            }
        });
    }
}

public static class DictionaryRlpConverterExt
{
    public static Dictionary<TKey, TValue> ReadDictionary<TKey, TValue, TKeyConverter, TValueConverter>(
        this ref RlpReader reader
    )
        where TKey : notnull
        where TKeyConverter : IRlpConverter<TKey>
        where TValueConverter : IRlpConverter<TValue>
        => DictionaryRlpConverter<TKey, TValue>.Read<TKeyConverter, TValueConverter>(ref reader);

    public static void Write<TKey, TValue, TKeyConverter, TValueConverter>(
        this ref RlpWriter writer,
        Dictionary<TKey, TValue> value
    )
        where TKey : notnull
        where TValueConverter : IRlpConverter<TValue>
        where TKeyConverter : IRlpConverter<TKey>
        => DictionaryRlpConverter<TKey, TValue>.Write<TKeyConverter, TValueConverter>(ref writer, value);
}
