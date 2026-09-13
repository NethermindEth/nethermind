// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>
/// Reads a genesis <c>alloc</c> map whose addresses may be written without the <c>0x</c> prefix.
/// </summary>
/// <remarks>
/// Both Geth and Besu write these keys unprefixed - Besu's own
/// <c>operator generate-blockchain-config</c> emits them that way - so the default address reader,
/// which requires the prefix, rejects a genesis that the client producing it considers valid. The
/// tolerance is deliberately scoped to this one map: an address arriving over JSON-RPC without the
/// prefix is still an error.
/// </remarks>
public sealed class GethGenesisAllocConverter : JsonConverter<Dictionary<Address, GethGenesisAllocJson>>
{
    private const string HexPrefix = "0x";

    public override Dictionary<Address, GethGenesisAllocJson>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected an object for the genesis alloc but found {reader.TokenType}.");
        }

        Dictionary<Address, GethGenesisAllocJson> alloc = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string key = reader.GetString() ?? throw new JsonException("Genesis alloc has an empty address.");
            reader.Read();
            GethGenesisAllocJson? account = JsonSerializer.Deserialize<GethGenesisAllocJson>(ref reader, options);
            if (account is not null)
            {
                alloc[ParseAddress(key)] = account;
            }
        }

        return alloc;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<Address, GethGenesisAllocJson> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach ((Address address, GethGenesisAllocJson account) in value)
        {
            writer.WritePropertyName(address.ToString());
            JsonSerializer.Serialize(writer, account, options);
        }

        writer.WriteEndObject();
    }

    private static Address ParseAddress(string key) =>
        new(key.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase) ? key : string.Concat(HexPrefix, key));
}
