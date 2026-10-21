// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
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
/// rejects a block or account override key that names no supported field, or two keys that name the same field, as the
/// spec requires. Keys match fields case-insensitively. <c>eth_call</c> and the other methods that take overrides keep
/// ignoring unknown keys.
/// </summary>
/// <remarks>The keys are compared as UTF-8 bytes on a copy of the reader, so the block is read and deserialized in one
/// pass, and nothing is allocated unless a key is rejected.</remarks>
public sealed class BlockStateCallConverter<T> : JsonConverter<BlockStateCall<T>>
{
    private static readonly OverrideFields BlockOverrideFields = OverrideFields.Of<BlockOverride>();
    private static readonly OverrideFields AccountOverrideFields = OverrideFields.Of<AccountOverride>();

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
                CheckKeys(reader, BlockOverrideFields, owner: default, hasOwner: false);
                result.BlockOverrides = TypeInfoJsonSerializer.Deserialize<BlockOverride>(ref reader, options);
            }
            else if (IsName(reader, "stateOverrides"u8))
            {
                reader.Read();
                CheckAccountKeys(reader);
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

    /// <summary>Checks the keys of each account of a <c>stateOverrides</c> object. Takes a copy of the reader.</summary>
    private static void CheckAccountKeys(Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            Utf8JsonReader address = reader;
            reader.Read();
            CheckKeys(reader, AccountOverrideFields, address, hasOwner: true);
            reader.Skip();
        }
    }

    /// <summary>Checks the keys of an override object. Takes a copy of the reader; <paramref name="owner"/> is
    /// positioned at the account address, read only to name it in an error.</summary>
    private static void CheckKeys(Utf8JsonReader reader, OverrideFields fields, Utf8JsonReader owner, bool hasOwner)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return;

        ulong seen = 0;
        Span<ulong> upperCase = stackalloc ulong[fields.Count];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            int field = fields.Find(reader, out ulong keyUpperCase);
            if (field < 0)
            {
                throw new JsonException($"unknown field \"{reader.GetString()}\" in {Describe(owner, hasOwner)}");
            }

            // The same spelling twice is one key given twice; only another spelling names the field again.
            ulong bit = 1UL << field;
            if ((seen & bit) != 0 && upperCase[field] != keyUpperCase)
            {
                throw new JsonException($"keys \"{fields.Spell(field, upperCase[field])}\" and \"{reader.GetString()}\" in {Describe(owner, hasOwner)} name the same field");
            }

            seen |= bit;
            upperCase[field] = keyUpperCase;
            reader.Read();
            reader.Skip();
        }
    }

    private static string Describe(Utf8JsonReader owner, bool hasOwner) =>
        hasOwner ? $"state override for {owner.GetString()}" : "block override";

    /// <summary>The JSON names of an override type's settable properties, compared ASCII case-insensitively.</summary>
    private sealed class OverrideFields
    {
        private readonly byte[][] _names;

        private OverrideFields(byte[][] names) => _names = names;

        public int Count => _names.Length;

        public static OverrideFields Of<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TOverride>()
        {
            byte[][] names = typeof(TOverride)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static property => property.SetMethod?.IsPublic == true
                    && property.GetCustomAttribute<JsonIgnoreAttribute>() is null
                    && (typeof(TOverride) != typeof(BlockOverride) || property.Name != nameof(BlockOverride.BeaconRoot)))
                .Select(static property => Encoding.UTF8.GetBytes(property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name))
                .ToArray();
            // One bit per field and per letter of a name.
            if (names.Length > 64 || names.Any(static name => name.Length > 64 || name.Any(static b => b >= 0x80)))
                throw new InvalidOperationException($"The override fields of {typeof(TOverride).Name} do not fit the key check.");
            return new OverrideFields(names);
        }

        /// <summary>The field the key at <paramref name="reader"/> names, or -1; <paramref name="upperCase"/> marks the
        /// positions of the key's upper-case letters, which tell its spellings apart.</summary>
        public int Find(in Utf8JsonReader reader, out ulong upperCase)
        {
            if (reader.ValueIsEscaped || reader.HasValueSequence)
            {
                return Find(Encoding.UTF8.GetBytes(reader.GetString()!), out upperCase);
            }

            return Find(reader.ValueSpan, out upperCase);
        }

        private int Find(ReadOnlySpan<byte> key, out ulong upperCase)
        {
            for (int i = 0; i < _names.Length; i++)
            {
                if (EqualsIgnoreCase(key, _names[i], out upperCase)) return i;
            }

            upperCase = 0;
            return -1;
        }

        /// <summary>Spells field <paramref name="field"/> with the upper-case letters <paramref name="upperCase"/> marks.</summary>
        public string Spell(int field, ulong upperCase)
        {
            byte[] name = _names[field];
            char[] chars = new char[name.Length];
            for (int i = 0; i < name.Length; i++)
            {
                char c = (char)name[i];
                chars[i] = (upperCase & (1UL << i)) != 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
            }

            return new string(chars);
        }

        /// <summary>ASCII case-insensitive equality; <paramref name="upperCase"/> marks the upper-case letters of
        /// <paramref name="key"/>.</summary>
        public static bool EqualsIgnoreCase(ReadOnlySpan<byte> key, ReadOnlySpan<byte> name, out ulong upperCase)
        {
            upperCase = 0;
            if (key.Length != name.Length) return false;

            for (int i = 0; i < key.Length; i++)
            {
                byte k = key[i];
                bool upper = k is >= (byte)'A' and <= (byte)'Z';
                if ((upper ? (byte)(k | 0x20) : k) != (byte)(name[i] | (name[i] is >= (byte)'A' and <= (byte)'Z' ? 0x20 : 0))) return false;
                if (upper) upperCase |= 1UL << i;
            }

            return true;
        }
    }
}
