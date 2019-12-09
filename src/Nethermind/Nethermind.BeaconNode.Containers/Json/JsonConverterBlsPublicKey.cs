using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2.Crypto;

namespace Nethermind.BeaconNode.Containers.Json
{
    public class JsonConverterBlsPublicKey : JsonConverter<BlsPublicKey>
    {
        public override BlsPublicKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new BlsPublicKey(reader.GetBytesFromPrefixedHex());
        }

        public override void Write(Utf8JsonWriter writer, BlsPublicKey value, JsonSerializerOptions options)
        {
            writer.WritePrefixedHexStringValue(value.AsSpan());
        }
    }
}
