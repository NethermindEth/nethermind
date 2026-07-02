// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Serialization.Json;

[JsonConverter(typeof(StorageKeysConverter))]
public sealed class StorageKeys : IReadOnlyCollection<UInt256>
{
    private readonly HashSet<UInt256> _keys = [];

    public int Count => _keys.Count;

    public void Add(UInt256 key) => _keys.Add(key);

    public HashSet<UInt256>.Enumerator GetEnumerator() => _keys.GetEnumerator();

    IEnumerator<UInt256> IEnumerable<UInt256>.GetEnumerator() => _keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _keys.GetEnumerator();
}

public sealed class StorageKeysConverter : JsonConverter<StorageKeys>
{
    public override StorageKeys Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return JsonSerializer.Deserialize<StorageKeys>(reader.GetString()!, options) ?? throw new JsonException();
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException();
        }

        StorageKeys keys = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            keys.Add(StorageIndexConverter.ReadValue(ref reader));
        }

        return keys;
    }

    public override void Write(Utf8JsonWriter writer, StorageKeys value, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}
