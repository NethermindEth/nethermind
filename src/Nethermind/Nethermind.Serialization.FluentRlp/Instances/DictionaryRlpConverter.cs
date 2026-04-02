// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Serialization.FluentRlp.Instances;

public abstract class DictionaryRlpConverter<TKey, TValue> where TKey : notnull
{
    public static Dictionary<TKey, TValue> Read(ref RlpReader reader, RefRlpReaderFunc<TKey> readKey, RefRlpReaderFunc<TValue> readValue)
    {
        var ctx = ValueTuple.Create(readKey, readValue);
        return reader.ReadSequence(ctx, static (scoped ref RlpReader r, (RefRlpReaderFunc<TKey>, RefRlpReaderFunc<TValue>) ctx) =>
        {
            Dictionary<TKey, TValue> result = [];
            while (r.HasNext)
            {

                (TKey key, TValue value) = r.ReadSequence(ctx, static (scoped ref RlpReader r, (RefRlpReaderFunc<TKey>, RefRlpReaderFunc<TValue>) ctx) =>
                {
                    var (readKey, readValue) = ctx;
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
        var ctx = ValueTuple.Create(value, writeKey, writeValue);
        writer.WriteSequence(ctx, static (ref RlpWriter w, (Dictionary<TKey, TValue>, RefRlpWriterAction<TKey>, RefRlpWriterAction<TValue>) ctx) =>
        {
            var (dictionary, writeKey, writeValue) = ctx;
            foreach (var kp in dictionary)
            {
                var innerCtx = ValueTuple.Create(kp, writeKey, writeValue);
                w.WriteSequence(innerCtx, static (ref RlpWriter w, (KeyValuePair<TKey, TValue>, RefRlpWriterAction<TKey>, RefRlpWriterAction<TValue>) ctx) =>
                {
                    var ((k, v), writeKey, writeValue) = ctx;
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
