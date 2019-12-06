using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.BeaconNode.Api
{
    internal class PrefixedHexByteArrayJsonConverter : JsonConverter<byte[]>
    {
        private const string Prefix = "0x";

        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (typeToConvert == typeof(string))
            {
                var hex = reader.GetString();
                if (!string.IsNullOrEmpty(hex) && hex.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return Enumerable.Range(Prefix.Length, hex.Length - Prefix.Length)
                         .Where(x => x % 2 == 0)
                         .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                         .ToArray();
                }
            }
            return new byte[0];
        }

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
        {
            var stringValue = Prefix + BitConverter.ToString(value).Replace("-", string.Empty);
            writer.WriteStringValue(stringValue);
        }
    }
}
