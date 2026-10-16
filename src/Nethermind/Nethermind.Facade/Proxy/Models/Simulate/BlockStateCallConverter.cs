// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
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
/// rejects a block or account override key that names no supported field, or two keys that name the same field, as the
/// spec requires. Keys match fields case-insensitively. <c>eth_call</c> and the other methods that take overrides keep
/// ignoring unknown keys.
/// </summary>
/// <remarks>The keys are checked on a copy of the reader, so the block is read and deserialized in one pass.</remarks>
public sealed class BlockStateCallConverter<T> : JsonConverter<BlockStateCall<T>>
{
    private static readonly FrozenDictionary<string, string> BlockOverrideFields = GetFields<BlockOverride>();
    private static readonly FrozenDictionary<string, string> AccountOverrideFields = GetFields<AccountOverride>();

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
            string? name = reader.GetString();
            reader.Read();
            if (IsName(name, nameof(BlockStateCall<T>.BlockOverrides)))
            {
                CheckKeys(reader, BlockOverrideFields, "block override");
                result.BlockOverrides = TypeInfoJsonSerializer.Deserialize<BlockOverride>(ref reader, options);
            }
            else if (IsName(name, nameof(BlockStateCall<T>.StateOverrides)))
            {
                CheckAccountKeys(reader);
                result.StateOverrides = TypeInfoJsonSerializer.Deserialize<Dictionary<Address, AccountOverride>>(ref reader, options);
            }
            else if (IsName(name, nameof(BlockStateCall<T>.Calls)))
            {
                result.Calls = TypeInfoJsonSerializer.Deserialize<T[]>(ref reader, options);
            }
            else
            {
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

    private static bool IsName(string? name, string expected) =>
        string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks the keys of each account of a <c>stateOverrides</c> object. Takes a copy of the reader.</summary>
    private static void CheckAccountKeys(Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string? address = reader.GetString();
            reader.Read();
            CheckKeys(reader, AccountOverrideFields, $"state override for {address}");
            reader.Skip();
        }
    }

    /// <summary>Checks the keys of an override object. Takes a copy of the reader.</summary>
    private static void CheckKeys(Utf8JsonReader reader, FrozenDictionary<string, string> fields, string what)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return;

        Dictionary<string, string>? seen = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string key = reader.GetString()!;
            if (!fields.TryGetValue(key, out string? field))
            {
                throw new JsonException($"unknown field \"{key}\" in {what}");
            }

            seen ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (seen.TryGetValue(field, out string? previous) && !string.Equals(previous, key, StringComparison.Ordinal))
            {
                throw new JsonException($"keys \"{previous}\" and \"{key}\" in {what} name the same field");
            }

            seen[field] = key;
            reader.Read();
            reader.Skip();
        }
    }

    private static FrozenDictionary<string, string> GetFields<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TOverride>() =>
        typeof(TOverride)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.SetMethod?.IsPublic == true && property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(static property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
            .ToFrozenDictionary(static name => name, static name => name, StringComparer.OrdinalIgnoreCase);
}
