// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.ParityStyle
{
    [JsonConverter(typeof(ParityTraceActionConverter))]
    public class ParityTraceAction
    {
        public int[]? TraceAddress { get; set; }
        public string? CallType { get; set; }

        public bool IncludeInTrace { get; set; } = true;
        public bool IsPrecompiled { get; set; }
        public string? Type { get; set; }
        public string? CreationMethod { get; set; }
        public Address? From { get; set; }
        public Address? To { get; set; }
        public long Gas { get; set; }
        public UInt256 Value { get; set; }
        public byte[]? Input { get; set; }
        public ParityTraceResult? Result { get; set; } = new();
        public List<ParityTraceAction> Subtraces { get; set; } = new();

        public Address? Author { get; set; }
        public string? RewardType { get; set; }
        public string? Error { get; set; }
    }

    /*
     * {
     *   "callType": "call",
     *   "from": "0x430adc807210dab17ce7538aecd4040979a45137",
     *   "gas": "0x1a1f8",
     *   "input": "0x",
     *   "to": "0x9bcb0733c56b1d8f0c7c4310949e00485cae4e9d",
     *   "value": "0x2707377c7552d8000"
     * },
     */
    public class ParityTraceActionConverter : JsonConverter<ParityTraceAction>
    {
        public override ParityTraceAction Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new ArgumentException($"Cannot deserialize {nameof(ParityTraceActionConverter)}.");
            }

            var value = new ParityTraceAction();

            reader.Read();
            while (reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new ArgumentException($"Cannot deserialize {nameof(ParityTraceActionConverter)}.");
                }

                if (reader.ValueTextEquals("callType"u8))
                {
                    reader.Read();
                    value.CallType = reader.GetString();
                }
                else if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    value.Type = reader.GetString();
                }
                else if (reader.ValueTextEquals("creationMethod"u8))
                {
                    reader.Read();
                    value.CreationMethod = reader.GetString();
                }
                else if (reader.ValueTextEquals("from"u8))
                {
                    reader.Read();
                    value.From = JsonSerializer.Deserialize<Address?>(ref reader, options);
                }
                else if (reader.ValueTextEquals("to"u8))
                {
                    reader.Read();
                    value.To = JsonSerializer.Deserialize<Address?>(ref reader, options);
                }
                else if (reader.ValueTextEquals("gas"u8))
                {
                    reader.Read();
                    value.Gas = JsonSerializer.Deserialize<long>(ref reader, options);
                }
                else if (reader.ValueTextEquals("value"u8))
                {
                    reader.Read();
                    value.Value = JsonSerializer.Deserialize<UInt256>(ref reader, options);
                }
                else if (reader.ValueTextEquals("input"u8))
                {
                    reader.Read();
                    value.Input = JsonSerializer.Deserialize<byte[]?>(ref reader, options);
                }
                else if (reader.ValueTextEquals("result"u8))
                {
                    reader.Read();
                    value.Result = JsonSerializer.Deserialize<ParityTraceResult?>(ref reader, options);
                }
                else if (reader.ValueTextEquals("subtraces"u8))
                {
                    reader.Read();
                    value.Subtraces = JsonSerializer.Deserialize<List<ParityTraceAction>>(ref reader, options);
                }
                else if (reader.ValueTextEquals("author"u8))
                {
                    reader.Read();
                    value.Author = JsonSerializer.Deserialize<Address?>(ref reader, options);
                }
                else if (reader.ValueTextEquals("rewardType"u8))
                {
                    reader.Read();
                    value.RewardType = JsonSerializer.Deserialize<string?>(ref reader, options);
                }
                else if (reader.ValueTextEquals("error"u8))
                {
                    reader.Read();
                    value.Error = JsonSerializer.Deserialize<string?>(ref reader, options);
                }
                else if (reader.ValueTextEquals("traceAddress"u8))
                {
                    reader.Read();
                    value.TraceAddress = JsonSerializer.Deserialize<int[]?>(ref reader, options);
                }
                else if (reader.ValueTextEquals("includeInTrace"u8))
                {
                    reader.Read();
                    value.IncludeInTrace = reader.GetBoolean();
                }
                else if (reader.ValueTextEquals("isPrecompiled"u8))
                {
                    reader.Read();
                    value.IsPrecompiled = reader.GetBoolean();
                }

                reader.Read();
            }

            return value;
        }

        public override void Write(
            Utf8JsonWriter writer,
            ParityTraceAction value,
            JsonSerializerOptions options)
        {
            if (value.Type == "reward")
            {
                WriteRewardJson(writer, value, options);
                return;
            }

            if (value.Type == "suicide")
            {
                WriteSelfDestructJson(writer, value, options);
                return;
            }
            writer.WriteStartObject();

            if (value.CallType != "create")
            {
                writer.WriteString("callType"u8, value.CallType);
            }
            else
            {
                writer.WriteString("creationMethod"u8, value.CreationMethod);
            }

            writer.WritePropertyName("from"u8);
            JsonSerializer.Serialize(writer, value.From, options);
            writer.WritePropertyName("gas"u8);
            JsonSerializer.Serialize(writer, value.Gas, options);

            writer.WritePropertyName("input"u8);
            JsonSerializer.Serialize(writer, value.Input, options);
            writer.WritePropertyName("to"u8);
            JsonSerializer.Serialize(writer, value.To, options);

            writer.WritePropertyName("value"u8);
            JsonSerializer.Serialize(writer, value.Value, options);

            writer.WriteEndObject();
        }

        private void WriteSelfDestructJson(Utf8JsonWriter writer, ParityTraceAction value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("address"u8);
            JsonSerializer.Serialize(writer, value.From, options);

            writer.WritePropertyName("balance"u8);
            JsonSerializer.Serialize(writer, value.Value, options);

            writer.WritePropertyName("refundAddress"u8);
            JsonSerializer.Serialize(writer, value.To, options);

            writer.WriteEndObject();
        }

        private void WriteRewardJson(Utf8JsonWriter writer, ParityTraceAction value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("author"u8);
            JsonSerializer.Serialize(writer, value.Author, options);

            writer.WritePropertyName("rewardType"u8);
            JsonSerializer.Serialize(writer, value.RewardType, options);

            writer.WritePropertyName("value"u8);
            JsonSerializer.Serialize(writer, value.Value, options);
            writer.WriteEndObject();
        }
    }
}
