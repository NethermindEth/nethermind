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
        byte[][] l = JsonSerializer.Deserialize<byte[][]>(ref reader, options);
        reader.Read();
        reader.Read();
        byte[][] r = JsonSerializer.Deserialize<byte[][]>(ref reader, options);
        reader.Read();
        reader.Read();
        byte[] a = JsonSerializer.Deserialize<byte[]>(ref reader, options);
        reader.Read();
        return new IpaProofStructSerialized(l, a, r);
    }

    public override void Write(Utf8JsonWriter writer, IpaProofStructSerialized value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("l"u8);
        JsonSerializer.Serialize(writer, value.L, options);

        writer.WritePropertyName("a"u8);
        JsonSerializer.Serialize(writer, value.A, options);

        writer.WritePropertyName("r"u8);
        JsonSerializer.Serialize(writer, value.R, options);

        writer.WriteEndObject();
    }
}