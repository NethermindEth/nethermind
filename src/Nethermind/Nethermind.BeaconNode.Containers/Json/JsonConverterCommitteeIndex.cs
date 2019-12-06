﻿using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Containers.Json
{
    public class JsonConverterCommitteeIndex : JsonConverter<CommitteeIndex>
    {
        public override CommitteeIndex Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new CommitteeIndex(reader.GetUInt64());
        }

        public override void Write(Utf8JsonWriter writer, CommitteeIndex value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue((ulong)value);
        }
    }
}
