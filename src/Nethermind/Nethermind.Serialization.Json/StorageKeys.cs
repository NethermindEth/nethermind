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
    private readonly List<UInt256> _keys = [];
    private readonly List<bool> _isFullWidth = [];

    public int Count => _keys.Count;

    public void Add(UInt256 key) => Add(key, isFullWidth: false);

    internal void Add(UInt256 key, bool isFullWidth)
    {
        _keys.Add(key);
        _isFullWidth.Add(isFullWidth);
    }

    /// <summary>Whether the key at <paramref name="index"/> was sent with 63 or more hex digits.</summary>
    /// <remarks>execution-apis <c>eth_getProof</c> echoes such a key as 32 bytes and any other as a quantity.</remarks>
    public bool IsFullWidth(int index) => _isFullWidth[index];

    public List<UInt256>.Enumerator GetEnumerator() => _keys.GetEnumerator();

    IEnumerator<UInt256> IEnumerable<UInt256>.GetEnumerator() => _keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _keys.GetEnumerator();
}

public sealed class StorageKeysConverter : JsonConverter<StorageKeys>
{
    private const int FullWidthMinLength = 2 + 63;

    public override StorageKeys Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return TypeInfoJsonSerializer.Deserialize<StorageKeys>(reader.GetString()!, options) ?? throw new JsonException();
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException();
        }

        StorageKeys keys = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("0x"u8))
            {
                keys.Add(UInt256.Zero);
                continue;
            }

            UInt256 key = StorageIndexConverter.ReadValue(ref reader);
            int length = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
            keys.Add(key, length >= FullWidthMinLength);
        }

        return keys;
    }

    public override void Write(Utf8JsonWriter writer, StorageKeys value, JsonSerializerOptions options) =>
        throw new NotSupportedException();
}
