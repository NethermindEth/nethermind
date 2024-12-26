// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Serialization.FluentRlp.Instances;

public abstract class DictionaryRlpConverter<TKey, TValue> where TKey : notnull
{
    public static Dictionary<TKey, TValue> Read(ref RlpReader reader, RefRlpReaderFunc<TKey> readKey, RefRlpReaderFunc<TValue> readValue)
    {
        return reader.ReadSequence((scoped ref RlpReader r) =>
        {
            Dictionary<TKey, TValue> result = [];
            while (r.HasNext)
            {
                (TKey key, TValue value) = r.ReadSequence((scoped ref RlpReader r) =>
                {
                    TKey key = readKey(ref r);
                    TValue value = readValue(ref r);

                    return (key, value);
                });

                result.Add(key, value);
            }

            return result;
        });
    }

    public static void Write(ref RlpWriter writer, Dictionary<TKey, TValue> value, RefRlpWriterAction<TKey> writeKey, RefRlpWriterAction<TValue> writeValue)
    {
        writer.WriteSequence((ref RlpWriter w) =>
        {
            foreach ((TKey k, TValue v) in value)
            {
                w.WriteSequence((ref RlpWriter w) =>
                {
                    writeKey(ref w, k);
                    writeValue(ref w, v);
                });
            }
        });
    }
}

public static class DictionaryRlpConverterExt
{
    public static Dictionary<TKey, TValue> ReadDictionary<TKey, TValue>(
        this ref RlpReader reader,
        RefRlpReaderFunc<TKey> readKey,
        RefRlpReaderFunc<TValue> readValue
    ) where TKey : notnull
        => DictionaryRlpConverter<TKey, TValue>.Read(ref reader, readKey, readValue);

    public static void Write<TKey, TValue>(
        this ref RlpWriter writer,
        Dictionary<TKey, TValue> value,
        RefRlpWriterAction<TKey> writeKey,
        RefRlpWriterAction<TValue> writeValue
    ) where TKey : notnull
        => DictionaryRlpConverter<TKey, TValue>.Write(ref writer, value, writeKey, writeValue);
}
