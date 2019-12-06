using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers.Json
{
    public class JsonConverterValidatorIndex : JsonConverter<ValidatorIndex>
    {
        public override ValidatorIndex Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new ValidatorIndex(reader.GetUInt64());
        }

        public override void Write(Utf8JsonWriter writer, ValidatorIndex value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue((ulong)value);
        }
    }
}
