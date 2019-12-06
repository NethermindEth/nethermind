using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.BeaconNode.Containers.Json
{
    public class JsonConverterShard : JsonConverter<Shard>
    {
        public override Shard Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new Shard(reader.GetUInt64());
        }

        public override void Write(Utf8JsonWriter writer, Shard value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue((ulong)value);
        }
    }
}
