// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

public class ParityAccountStateChangeJsonConverter : JsonConverter<ParityAccountStateChange>
{
    private readonly Bytes32Converter _32BytesConverter = new();
    private readonly UInt256Converter _storageKeyConverter = new();

    public override ParityAccountStateChange Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Cannot deserialize {nameof(ParityAccountStateChange)}.");
        }

        ParityAccountStateChange value = new();

        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals("balance"u8))
            {
                reader.Read();
                value.Balance = ReadChange<UInt256?>(ref reader, options);
            }
            else if (reader.ValueTextEquals("code"u8))
            {
                reader.Read();
                value.Code = ReadChange<byte[]>(ref reader, options);
            }
            else if (reader.ValueTextEquals("nonce"u8))
            {
                reader.Read();
                value.Nonce = ReadChange<UInt256?>(ref reader, options);
            }
            else if (reader.ValueTextEquals("storage"u8))
            {
                reader.Read();
                value.Storage = ReadStorage(ref reader, options);
            }
            else
            {
                // A member from a newer format; skipping it keeps the block readable after a downgrade.
                reader.Skip();
            }

            reader.Read();
        }

        return value;
    }

    private Dictionary<UInt256, ParityStateChange<byte[]>> ReadStorage(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Cannot deserialize {nameof(ParityAccountStateChange)}.");
        }

        Dictionary<UInt256, ParityStateChange<byte[]>> storage = [];

        reader.Read();
        while (reader.TokenType != JsonTokenType.EndObject)
        {
            UInt256 index = _storageKeyConverter.ReadAsPropertyName(ref reader, typeof(UInt256), options);
            reader.Read();
            storage[index] = ReadChange<byte[]>(ref reader, options);
            reader.Read();
        }

        return storage;
    }

    /// <summary>Reads <c>"="</c>, <c>{"+": after}</c>, <c>{"-": before}</c> or <c>{"*": {"from": before, "to": after}}</c>.</summary>
    private static ParityStateChange<T>? ReadChange<T>(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("="u8))
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Cannot deserialize {nameof(ParityStateChange<T>)}.");
        }

        ParityStateChange<T> change = new(default, default);

        reader.Read();
        if (reader.ValueTextEquals("+"u8))
        {
            reader.Read();
            change.After = TypeInfoJsonSerializer.Deserialize<T>(ref reader, options);
        }
        else if (reader.ValueTextEquals("-"u8))
        {
            reader.Read();
            change.Before = TypeInfoJsonSerializer.Deserialize<T>(ref reader, options);
        }
        else if (reader.ValueTextEquals("*"u8))
        {
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"Cannot deserialize {nameof(ParityStateChange<T>)}.");
            }

            bool hasFrom = false;
            bool hasTo = false;

            reader.Read();
            while (reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.ValueTextEquals("from"u8))
                {
                    reader.Read();
                    change.Before = TypeInfoJsonSerializer.Deserialize<T>(ref reader, options);
                    hasFrom = true;
                }
                else if (reader.ValueTextEquals("to"u8))
                {
                    reader.Read();
                    change.After = TypeInfoJsonSerializer.Deserialize<T>(ref reader, options);
                    hasTo = true;
                }
                else
                {
                    reader.Skip();
                }

                reader.Read();
            }

            // Without either side, a change would read back as a creation or deletion.
            if (!hasFrom || !hasTo)
            {
                throw new JsonException($"Cannot deserialize {nameof(ParityStateChange<T>)}.");
            }
        }
        else
        {
            throw new JsonException($"Cannot deserialize {nameof(ParityStateChange<T>)}.");
        }

        reader.Read();
        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException($"Cannot deserialize {nameof(ParityStateChange<T>)}.");
        }

        return change;
    }

    private static void WriteChange(Utf8JsonWriter writer, ParityStateChange<byte[]> change, JsonSerializerOptions options)
    {
        if (change is null)
        {
            writer.WriteStringValue("="u8);
        }
        else
        {
            if (change.Before is null)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("+"u8);
                TypeInfoJsonSerializer.Serialize(writer, change.After, options);
                writer.WriteEndObject();
            }
            else if (change.After is null)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("-"u8);
                TypeInfoJsonSerializer.Serialize(writer, change.Before, options);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject();
                writer.WritePropertyName("*"u8);
                writer.WriteStartObject();
                writer.WritePropertyName("from"u8);
                TypeInfoJsonSerializer.Serialize(writer, change.Before, options);
                writer.WritePropertyName("to"u8);
                TypeInfoJsonSerializer.Serialize(writer, change.After, options);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        }
    }

    private static void WriteChange(Utf8JsonWriter writer, ParityStateChange<UInt256?> change, JsonSerializerOptions options)
    {
        if (change is null)
        {
            writer.WriteStringValue("="u8);
        }
        else
        {
            if (change.Before is null)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("+"u8);
                TypeInfoJsonSerializer.Serialize(writer, change.After, options);
                writer.WriteEndObject();
            }
            else if (change.After is null)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("-"u8);
                TypeInfoJsonSerializer.Serialize(writer, change.Before, options);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject();
                writer.WritePropertyName("*"u8);
                writer.WriteStartObject();
                writer.WritePropertyName("from"u8);
                TypeInfoJsonSerializer.Serialize(writer, change.Before, options);
                writer.WritePropertyName("to"u8);
                TypeInfoJsonSerializer.Serialize(writer, change.After, options);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        }
    }

    private void WriteStorageChange(Utf8JsonWriter writer, ParityStateChange<byte[]> change, bool isNew, JsonSerializerOptions options)
    {
        if (change is null)
        {
            writer.WriteStringValue("="u8);
        }
        else
        {
            if (isNew)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("+"u8);
                _32BytesConverter.Write(writer, change.After, options);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject();
                writer.WritePropertyName("*"u8);
                writer.WriteStartObject();
                writer.WritePropertyName("from"u8);
                _32BytesConverter.Write(writer, change.Before, options);
                writer.WritePropertyName("to"u8);
                _32BytesConverter.Write(writer, change.After, options);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        ParityAccountStateChange value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("balance"u8);
        if (value.Balance is null)
        {
            writer.WriteStringValue("="u8);
        }
        else
        {
            WriteChange(writer, value.Balance, options);
        }

        writer.WritePropertyName("code"u8);
        if (value.Code is not null)
        {
            WriteChange(writer, value.Code, options);
        }
        else if (value.Balance is { Before: null, After: not null } or { Before: not null, After: null })
        {
            // A created or deleted account always reports balance null <-> X, but StateProvider only reports code when
            // either side is non-empty, so an unreported code change there means empty code was added or removed.
            writer.WriteStartObject();
            writer.WritePropertyName(value.Balance.Before is null ? "+"u8 : "-"u8);
            writer.WriteStringValue("0x"u8);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteStringValue("="u8);
        }

        writer.WritePropertyName("nonce"u8);
        if (value.Nonce is null)
        {
            writer.WriteStringValue("="u8);
        }
        else
        {
            WriteChange(writer, value.Nonce, options);
        }

        writer.WritePropertyName("storage"u8);

        writer.WriteStartObject();
        if (value.Storage is not null)
        {
            WriteStorage(writer, value.Storage, value.Balance?.Before is null && value.Balance?.After is not null, options);
        }

        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>Writes the slots in ascending key order, sorting them in a pooled buffer.</summary>
    [SkipLocalsInit]
    private void WriteStorage(Utf8JsonWriter writer, Dictionary<UInt256, ParityStateChange<byte[]>> storage, bool isNew, JsonSerializerOptions options)
    {
        Span<byte> keyBytes = stackalloc byte[32];
        Span<byte> keyName = stackalloc byte[2 + 64];
        keyName[0] = (byte)'0';
        keyName[1] = (byte)'x';
        Span<byte> keyHex = keyName[2..];

        using ArrayPoolListRef<KeyValuePair<UInt256, ParityStateChange<byte[]>>> sorted = new(storage.Count, storage);
        sorted.Sort(static (x, y) => x.Key.CompareTo(y.Key));
        foreach ((UInt256 key, ParityStateChange<byte[]> change) in sorted.AsSpan())
        {
            key.ToBigEndian(keyBytes);
            keyBytes.OutputBytesToByteHex(keyHex, false);
            writer.WritePropertyName(keyName);
            WriteStorageChange(writer, change, isNew, options);
        }
    }
}
