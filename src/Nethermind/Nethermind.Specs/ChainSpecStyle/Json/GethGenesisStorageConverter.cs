// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>
/// Reads genesis account storage like Geth: slot keys are 32-byte hex strings, with or without a <c>0x</c> prefix.
/// </summary>
public class GethGenesisStorageConverter : JsonConverter<Dictionary<UInt256, byte[]>>
{
    public override void Write(Utf8JsonWriter writer, Dictionary<UInt256, byte[]> value, JsonSerializerOptions options) =>
        throw new NotSupportedException();

    public override Dictionary<UInt256, byte[]> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Cannot deserialize genesis storage.");
        }

        Dictionary<UInt256, byte[]> storage = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            // An unprefixed key is always hex, never a decimal number.
            byte[] key = Bytes.FromHexString(reader.GetString()!);
            if (key.Length > 32)
            {
                throw new JsonException("Genesis storage key is longer than 32 bytes.");
            }

            reader.Read();
            storage[key.ToUInt256()] = JsonSerializer.Deserialize<byte[]>(ref reader, options)!;
        }

        return storage;
    }
}
