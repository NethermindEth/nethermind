using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2.Crypto;

namespace Nethermind.BeaconNode.Containers.Json
{
    public class JsonConverterBlsSignature : JsonConverter<BlsSignature>
    {
        public override BlsSignature Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new BlsSignature(reader.GetBytesFromPrefixedHex());
        }

        public override void Write(Utf8JsonWriter writer, BlsSignature value, JsonSerializerOptions options)
        {
            writer.WritePrefixedHexStringValue(value.AsSpan());
        }
    }
}
