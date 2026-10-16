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
/// Reads a block of <c>eth_simulateV1</c> and rejects a block or account override key that names no supported field,
/// or two keys that name the same field, as the spec requires. Keys match fields case-insensitively. Other
/// methods that take overrides keep ignoring unknown keys.
/// </summary>
public sealed class BlockStateCallConverter<T> : JsonConverter<BlockStateCall<T>>
{
    private static readonly FrozenDictionary<string, string> BlockOverrideFields = GetFields<BlockOverride>();
    private static readonly FrozenDictionary<string, string> AccountOverrideFields = GetFields<AccountOverride>();

    public override BlockStateCall<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Expected an object for a block state call, got {root.ValueKind}.");
        }

        BlockStateCall<T> result = new();
        foreach (JsonProperty property in root.EnumerateObject())
        {
            JsonElement value = property.Value;
            if (IsName(property, nameof(BlockStateCall<T>.BlockOverrides)))
            {
                CheckKeys(value, BlockOverrideFields, "block override");
                result.BlockOverrides = TypeInfoJsonSerializer.Deserialize<BlockOverride>(value, options);
            }
            else if (IsName(property, nameof(BlockStateCall<T>.StateOverrides)))
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty account in value.EnumerateObject())
                    {
                        CheckKeys(account.Value, AccountOverrideFields, $"state override for {account.Name}");
                    }
                }

                result.StateOverrides = TypeInfoJsonSerializer.Deserialize<Dictionary<Address, AccountOverride>>(value, options);
            }
            else if (IsName(property, nameof(BlockStateCall<T>.Calls)))
            {
                result.Calls = TypeInfoJsonSerializer.Deserialize<T[]>(value, options);
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

    private static bool IsName(JsonProperty property, string name) =>
        string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase);

    private static void CheckKeys(JsonElement overrides, FrozenDictionary<string, string> fields, string what)
    {
        if (overrides.ValueKind != JsonValueKind.Object) return;

        Dictionary<string, string>? seen = null;
        foreach (JsonProperty property in overrides.EnumerateObject())
        {
            if (!fields.TryGetValue(property.Name, out string? field))
            {
                throw new JsonException($"unknown field \"{property.Name}\" in {what}");
            }

            seen ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (seen.TryGetValue(field, out string? previous) && !string.Equals(previous, property.Name, StringComparison.Ordinal))
            {
                throw new JsonException($"keys \"{previous}\" and \"{property.Name}\" in {what} name the same field");
            }

            seen[field] = property.Name;
        }
    }

    private static FrozenDictionary<string, string> GetFields<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TOverride>() =>
        typeof(TOverride)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.SetMethod?.IsPublic == true && property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(static property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
            .ToFrozenDictionary(static name => name, static name => name, StringComparer.OrdinalIgnoreCase);
}
