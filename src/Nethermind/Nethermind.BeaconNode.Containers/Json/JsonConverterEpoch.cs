using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers.Json
{
    public class JsonConverterEpoch : JsonConverter<Epoch>
    {
        public override Epoch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new Epoch(reader.GetUInt64());
        }

        public override void Write(Utf8JsonWriter writer, Epoch value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue((ulong)value);
        }
    }
}
