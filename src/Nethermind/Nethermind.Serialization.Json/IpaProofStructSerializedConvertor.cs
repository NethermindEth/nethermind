using System;
using System.Text.Json;
using Nethermind.Verkle.Proofs;

namespace Nethermind.Serialization.Json;
public class IpaProofStructSerializedConverter : System.Text.Json.Serialization.JsonConverter<IpaProofStructSerialized>
{
    public override IpaProofStructSerialized Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        reader.Read();
        reader.Read();
        var lData = ByteArrayConverter.Convert(ref reader); // Use ByteArrayConverter for 'l'
        var l = lData != null ? JsonSerializer.Deserialize<byte[][]>(lData, options) : null;
        reader.Read();
        reader.Read();
        var aData = ByteArrayConverter.Convert(ref reader); // Use ByteArrayConverter for 'a'
        var a = aData != null ? JsonSerializer.Deserialize<byte[]>(aData, options) : null;
        reader.Read();
        reader.Read(); // Skip over the 'r' property name
        var rData = ByteArrayConverter.Convert(ref reader); // Use ByteArrayConverter for 'r'
        var r = rData != null ? JsonSerializer.Deserialize<byte[][]>(rData, options) : null;

        return new IpaProofStructSerialized(l, a, r);
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