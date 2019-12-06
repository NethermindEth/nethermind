using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.BeaconNode.Containers.Json
{
    public class JsonConverterDomain : JsonConverter<Domain>
    {
        public override Domain Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new Domain(reader.GetBytesFromPrefixedHex());
        }

        public override void Write(Utf8JsonWriter writer, Domain value, JsonSerializerOptions options)
        {
            writer.WritePrefixedHexStringValue(value.AsSpan());
        }
    }
}
