using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers.Json
{
    public class JsonConverterGwei : JsonConverter<Gwei>
    {
        public override Gwei Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new Gwei(reader.GetUInt64());
        }

        public override void Write(Utf8JsonWriter writer, Gwei value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue((ulong)value);
        }
    }
}
