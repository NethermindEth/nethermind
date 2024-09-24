using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
using Nethermind.Serialization.Json;

namespace Nethermind.Evm.Tracing.OpcodeStats;

public class OpcodeStatsTraceConvertor : JsonConverter<OpcodeStatsTxTrace>
{
    public override OpcodeStatsTxTrace Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, OpcodeStatsTxTrace value, JsonSerializerOptions options)
    {

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        {
            writer.WriteStartObject();

            writer.WritePropertyName("initialBlockNumber"u8);
            JsonSerializer.Serialize(writer, value.InitialBlockNumber, options);
            writer.WritePropertyName("currentBlockNumber"u8);
            JsonSerializer.Serialize(writer, value.CurrentBlockNumber, options);
            writer.WritePropertyName("errorPerItem"u8);
            JsonSerializer.Serialize(writer, value.ErrorPerItem, options);
            writer.WritePropertyName("confidence"u8);
            JsonSerializer.Serialize(writer, value.Confidence, options);

            if (value.Entries is not null)
            {
                writer.WritePropertyName("stats"u8);
                writer.WriteStartArray();
                foreach (var OpCodePattern in value.Entries)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("pattern"u8);
                    writer.WriteStringValue(OpCodePattern.Pattern);

                    writer.WritePropertyName("bytes"u8);
                    writer.WriteStartArray();
                    foreach (var opCode in OpCodePattern.Bytes)
                        writer.WriteNumberValue((byte)opCode);
                    writer.WriteEndArray();

                    writer.WritePropertyName("count"u8);
                    JsonSerializer.Serialize(writer, OpCodePattern.Count, options);
                    writer.WriteEndObject();

                }
                writer.WriteEndArray();

            }

            writer.WriteEndObject();
        }
    }

}
