using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.PatternAnalyzer.Plugin.Tracer.Pattern;

public class PatternStatsTraceConvertor : JsonConverter<PatternAnalyzerTxTrace>
{
    public override PatternAnalyzerTxTrace Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, PatternAnalyzerTxTrace value, JsonSerializerOptions options)
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
                foreach (var opCodePattern in value.Entries)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("pattern"u8);
                    writer.WriteStringValue(opCodePattern.Pattern);

                    writer.WritePropertyName("bytes"u8);
                    writer.WriteStartArray();
                    foreach (var opCode in opCodePattern.Bytes)
                        writer.WriteNumberValue(opCode);
                    writer.WriteEndArray();

                    writer.WritePropertyName("count"u8);
                    JsonSerializer.Serialize(writer, opCodePattern.Count, options);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }
    }
}
