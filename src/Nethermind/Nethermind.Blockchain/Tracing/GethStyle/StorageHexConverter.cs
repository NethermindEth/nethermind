// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Serializes a struct-log storage snapshot kept as <see cref="UInt256"/> domain objects into the
/// Geth-style <c>{ "0x..key..": "0x..value.." }</c> shape (0x-prefixed 32-byte words) without allocating
/// per-entry hex strings during tracing — values are formatted only here, at write time.
/// </summary>
public sealed class StorageHexConverter : JsonConverter<Dictionary<UInt256, UInt256>>
{
    public override void Write(Utf8JsonWriter writer, Dictionary<UInt256, UInt256> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<UInt256, UInt256> slot in value)
        {
            HexWriter.WriteUInt256HexPropertyName(writer, slot.Key, zeroPadded: true, addHexPrefix: true);
            HexWriter.WriteUInt256HexRawValue(writer, slot.Value, zeroPadded: true, addHexPrefix: true);
        }
        writer.WriteEndObject();
    }

    public override Dictionary<UInt256, UInt256> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Dictionary<UInt256, UInt256> storage = [];
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            UInt256 key = new(Bytes.FromHexString(reader.GetString()!), isBigEndian: true);
            reader.Read();
            storage[key] = new UInt256(Bytes.FromHexString(reader.GetString()!), isBigEndian: true);
        }
        return storage;
    }
}
