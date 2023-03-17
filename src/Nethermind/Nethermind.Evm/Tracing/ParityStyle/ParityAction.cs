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
    [System.Text.Json.Serialization.JsonConverter(typeof(ParityTraceActionConverter))]
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

    public class ParityTraceActionConverter : JsonConverter<ParityTraceAction>
    {
        public override ParityTraceAction Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

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

            if (value.CallType == "create")
            {
                writer.WritePropertyName("init"u8);
                JsonSerializer.Serialize(writer, value.Input, options);
            }
            else
            {
                writer.WritePropertyName("input"u8);
                JsonSerializer.Serialize(writer, value.Input, options);
                writer.WritePropertyName("to"u8);
                JsonSerializer.Serialize(writer, value.To, options);
            }

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
