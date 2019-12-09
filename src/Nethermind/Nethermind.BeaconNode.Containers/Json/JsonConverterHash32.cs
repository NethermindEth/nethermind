using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers.Json
{
    public class JsonConverterHash32 : JsonConverter<Hash32>
    {
        public override Hash32 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new Hash32(reader.GetBytesFromPrefixedHex());
        }

        public override void Write(Utf8JsonWriter writer, Hash32 value, JsonSerializerOptions options)
        {
            writer.WritePrefixedHexStringValue(value.AsSpan());
        }
    }
}
