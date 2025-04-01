using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.PatternAnalyzer.Plugin.Tracer.Call;

public class CallAnalyzerTxTraceConvertor : JsonConverter<CallAnalyzerTxTrace>
{
    public override CallAnalyzerTxTrace Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, CallAnalyzerTxTrace value, JsonSerializerOptions options)
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

            if (value.Entries is not null)
            {
                writer.WritePropertyName("stats"u8);
                writer.WriteStartArray();
                foreach (var callStats in value.Entries)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("address"u8);
                    writer.WriteStringValue(callStats.Address);

                    writer.WritePropertyName("count"u8);
                    JsonSerializer.Serialize(writer, callStats.Count, options);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }
    }
}
