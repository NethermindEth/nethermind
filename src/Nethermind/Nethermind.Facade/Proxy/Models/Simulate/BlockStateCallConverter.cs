// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;

namespace Nethermind.Facade.Proxy.Models.Simulate;

/// <summary>Creates the <see cref="BlockStateCallConverter{T}"/> for each <see cref="BlockStateCall{T}"/>.</summary>
public sealed class BlockStateCallConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(BlockStateCall<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        typeToConvert == typeof(BlockStateCall<TransactionForRpc>) ? new BlockStateCallConverter<TransactionForRpc>()
        : typeToConvert == typeof(BlockStateCall<TransactionWithSourceDetails>) ? new BlockStateCallConverter<TransactionWithSourceDetails>()
        : throw new NotSupportedException($"No converter for {typeToConvert}.");
}

/// <summary>
/// Reads a block of a simulate request (<c>eth_simulateV1</c>, <c>debug_simulateV1</c> and <c>trace_simulateV1</c>) and
/// rejects a block override key that names no supported field, or two keys that name the same field, as the spec
/// requires. Keys match fields case-insensitively. <see cref="AccountOverrideConverter"/> checks the keys of an account
/// override.
/// </summary>
/// <remarks>The keys are compared as UTF-8 bytes on a copy of the reader, so the block is read and deserialized in one
/// pass, and nothing is allocated unless a key is rejected.</remarks>
public sealed class BlockStateCallConverter<T> : JsonConverter<BlockStateCall<T>>
{
    private static readonly OverrideFields BlockOverrideFields = OverrideFields.Of<BlockOverride>();

    public override BlockStateCall<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected an object for a block state call, got {reader.TokenType}.");
        }

        BlockStateCall<T> result = new();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (IsName(reader, "blockOverrides"u8))
            {
                reader.Read();
                CheckBlockKeys(reader);
                result.BlockOverrides = TypeInfoJsonSerializer.Deserialize<BlockOverride>(ref reader, options);
            }
            else if (IsName(reader, "stateOverrides"u8))
            {
                reader.Read();
                result.StateOverrides = TypeInfoJsonSerializer.Deserialize<Dictionary<Address, AccountOverride>>(ref reader, options);
            }
            else if (IsName(reader, "calls"u8))
            {
                reader.Read();
                result.Calls = TypeInfoJsonSerializer.Deserialize<T[]>(ref reader, options);
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, BlockStateCall<T> value, JsonSerializerOptions options)
    {
        bool skipNull = options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull;
        writer.WriteStartObject();
        WriteProperty(writer, nameof(BlockStateCall<T>.BlockOverrides), value.BlockOverrides, skipNull, options);
        WriteProperty(writer, nameof(BlockStateCall<T>.StateOverrides), value.StateOverrides, skipNull, options);
        WriteProperty(writer, nameof(BlockStateCall<T>.Calls), value.Calls, skipNull, options);
        writer.WriteEndObject();
    }

    private static void WriteProperty<TValue>(Utf8JsonWriter writer, string name, TValue value, bool skipNull, JsonSerializerOptions options)
    {
        if (value is null && skipNull) return;

        writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(name) ?? name);
        TypeInfoJsonSerializer.Serialize(writer, value, options);
    }

    private static bool IsName(in Utf8JsonReader reader, ReadOnlySpan<byte> expected) =>
        reader.ValueIsEscaped || reader.HasValueSequence
            ? string.Equals(reader.GetString(), Encoding.UTF8.GetString(expected), StringComparison.OrdinalIgnoreCase)
            : OverrideFields.EqualsIgnoreCase(reader.ValueSpan, expected, out _);

    /// <summary>Checks the keys of a <c>blockOverrides</c> object. Takes a copy of the reader.</summary>
    private static void CheckBlockKeys(Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return;

        ulong seen = 0;
        Span<ulong> upperCase = stackalloc ulong[BlockOverrideFields.Count];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            BlockOverrideFields.Match(reader, ref seen, upperCase, "block override");
            reader.Read();
            reader.Skip();
        }
    }
}
