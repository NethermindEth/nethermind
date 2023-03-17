// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Evm.Tracing.GethStyle
{
    [JsonConverter(typeof(GethLikeTxTraceJsonConverter))]
    public class GethLikeTxTrace
    {
        public Stack<Dictionary<string, string>> StoragesByDepth { get; } = new();

        public GethLikeTxTrace()
        {
            Entries = new List<GethTxTraceEntry>();
        }

        public long Gas { get; set; }

        public bool Failed { get; set; }

        public byte[] ReturnValue { get; set; }

        [Newtonsoft.Json.JsonProperty(PropertyName = "structLogs")]
        public List<GethTxTraceEntry> Entries { get; set; }
    }

    public class GethLikeTxTraceJsonConverter : JsonConverter<GethLikeTxTrace>
    {
        public override GethLikeTxTrace Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        public override void Write(
            Utf8JsonWriter writer,
            GethLikeTxTrace value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            if (value.StoragesByDepth.Count > 0)
            {
                writer.WritePropertyName("structLogs"u8);
                JsonSerializer.Serialize(writer, value.StoragesByDepth, options);
            }

            writer.WritePropertyName("gas"u8);
            JsonSerializer.Serialize(writer, value.Gas, options);

            writer.WritePropertyName("failed"u8);
            JsonSerializer.Serialize(writer, value.Failed, options);

            writer.WritePropertyName("returnValue"u8);
            JsonSerializer.Serialize(writer, value.ReturnValue, options);

            writer.WritePropertyName("structLogs"u8);
            JsonSerializer.Serialize(writer, value.Entries, options);

            writer.WriteEndObject();
        }
    }
}
