using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers.Json
{
    public class JsonConverterBytes32 : JsonConverter<Bytes32>
    {
        public override Bytes32 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new Bytes32(reader.GetBytesFromPrefixedHex());
        }

        public override void Write(Utf8JsonWriter writer, Bytes32 value, JsonSerializerOptions options)
        {
            writer.WritePrefixedHexStringValue(value.AsSpan());
        }
    }
}
