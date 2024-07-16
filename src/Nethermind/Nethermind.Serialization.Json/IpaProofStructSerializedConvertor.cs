using System;
using System.Text.Json;
using Nethermind.Verkle.Proofs;

namespace Nethermind.Serialization.Json;
public class IpaProofStructSerializedConverter : System.Text.Json.Serialization.JsonConverter<IpaProofStructSerialized>
{
    public override IpaProofStructSerialized Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        byte[][] cl = [];
        byte[][] cr = [];

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            for (int i = 0; i < 2; i++)
            {
                reader.Read();
                if (reader.ValueTextEquals("cl"u8))
                {
                    reader.Read();
                    cl = JsonSerializer.Deserialize<byte[][]>(ref reader, options) ??
                         throw new InvalidOperationException();
                }
                else if (reader.ValueTextEquals("cr"u8))
                {
                    reader.Read();
                    cr = JsonSerializer.Deserialize<byte[][]>(ref reader, options) ??
                         throw new InvalidOperationException();
                }
            }
        }
        reader.Read();
        byte[] hex = JsonSerializer.Deserialize<byte[]>(ref reader, options);
        reader.Read();
        return new IpaProofStructSerialized(cl, hex, cr);
    }

    public override void Write(Utf8JsonWriter writer, IpaProofStructSerialized value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("l");
        var lBytes = JsonSerializer.SerializeToUtf8Bytes(value.L, options);
        ByteArrayConverter.Convert(writer, lBytes, skipLeadingZeros: false);

        writer.WritePropertyName("a");
        var aBytes = JsonSerializer.SerializeToUtf8Bytes(value.A, options);
        ByteArrayConverter.Convert(writer, aBytes, skipLeadingZeros: false);

        writer.WritePropertyName("r");
        var rBytes = JsonSerializer.SerializeToUtf8Bytes(value.R, options);
        ByteArrayConverter.Convert(writer, rBytes, skipLeadingZeros: false);

        writer.WriteEndObject();
    }
}
