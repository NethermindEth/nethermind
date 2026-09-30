// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
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

    public override ParityAccountStateChange Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        ParityAccountStateChange change = new()
        {
            Balance = ReadChange<UInt256?>(value.GetProperty("balance"), options),
            Code = ReadChange<byte[]>(value.GetProperty("code"), options),
            Nonce = ReadChange<UInt256?>(value.GetProperty("nonce"), options),
            Storage = [],
        };
        foreach (JsonProperty slot in value.GetProperty("storage").EnumerateObject())
        {
            UInt256 index = UInt256Converter.ReadHex(Encoding.UTF8.GetBytes(slot.Name));
            change.Storage[index] = ReadChange<byte[]>(slot.Value, options)!;
        }
        return change;
    }

    private static ParityStateChange<T>? ReadChange<T>(JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() == "=") return null;
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException();
        JsonElement.ObjectEnumerator fields = value.EnumerateObject();
        if (!fields.MoveNext()) throw new JsonException();
        JsonProperty marker = fields.Current;
        if (fields.MoveNext()) throw new JsonException();
        return marker.Name switch
        {
            "+" => new ParityStateChange<T>(default, marker.Value.Deserialize<T>(options)),
            "-" => new ParityStateChange<T>(marker.Value.Deserialize<T>(options), default),
            "*" => new ParityStateChange<T>(
                marker.Value.GetProperty("from").Deserialize<T>(options),
                marker.Value.GetProperty("to").Deserialize<T>(options)),
            _ => throw new JsonException(),
        };
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
                JsonSerializer.Serialize(writer, change.After, options);
                writer.WriteEndObject();
            }
            else if (change.After is null)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("-"u8);
                JsonSerializer.Serialize(writer, change.Before, options);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject();
                writer.WritePropertyName("*"u8);
                writer.WriteStartObject();
                writer.WritePropertyName("from"u8);
                JsonSerializer.Serialize(writer, change.Before, options);
                writer.WritePropertyName("to"u8);
                JsonSerializer.Serialize(writer, change.After, options);
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
                JsonSerializer.Serialize(writer, change.After, options);
                writer.WriteEndObject();
            }
            else if (change.After is null)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("-"u8);
                JsonSerializer.Serialize(writer, change.Before, options);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject();
                writer.WritePropertyName("*"u8);
                writer.WriteStartObject();
                writer.WritePropertyName("from"u8);
                JsonSerializer.Serialize(writer, change.Before, options);
                writer.WritePropertyName("to"u8);
                JsonSerializer.Serialize(writer, change.After, options);
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
