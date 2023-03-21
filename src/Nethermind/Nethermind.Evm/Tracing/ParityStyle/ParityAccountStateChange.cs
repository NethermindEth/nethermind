// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

using Nethermind.Int256;
using Nethermind.Serialization.Json;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    [JsonConverter(typeof(ParityAccountStateChangeJsonConverter))]
    public class ParityAccountStateChange
    {
        public ParityStateChange<byte[]> Code { get; set; }
        public ParityStateChange<UInt256?> Balance { get; set; }
        public ParityStateChange<UInt256?> Nonce { get; set; }
        public Dictionary<UInt256, ParityStateChange<byte[]>> Storage { get; set; }
    }

    public class ParityAccountStateChangeJsonConverter : JsonConverter<ParityAccountStateChange>
    {
        private Bytes32Converter _32BytesConverter = new();

        public override ParityAccountStateChange Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        private void WriteChange(Utf8JsonWriter writer, ParityStateChange<byte[]> change, JsonSerializerOptions options)
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

        private void WriteChange(Utf8JsonWriter writer, ParityStateChange<UInt256?> change, JsonSerializerOptions options)
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
            if (value.Code is null)
            {
                writer.WriteStringValue("="u8);
            }
            else
            {
                WriteChange(writer, value.Code, options);
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
                foreach (KeyValuePair<UInt256, ParityStateChange<byte[]>> pair in value.Storage.OrderBy(s => s.Key))
                {
                    string trimmedKey = pair.Key.ToString("x64");
                    trimmedKey = trimmedKey.Substring(trimmedKey.Length - 64, 64);

                    writer.WritePropertyName(string.Concat("0x", trimmedKey));
                    WriteStorageChange(writer, pair.Value, value.Balance?.Before is null && value.Balance?.After is not null, options);
                }
            }

            writer.WriteEndObject();

            writer.WriteEndObject();
        }
    }
}
