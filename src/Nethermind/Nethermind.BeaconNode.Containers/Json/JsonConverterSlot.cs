﻿using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Containers.Json
{
    public class JsonConverterSlot : JsonConverter<Slot>
    {
        public override Slot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new Slot(reader.GetUInt64());
        }

        public override void Write(Utf8JsonWriter writer, Slot value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue((ulong)value);
        }
    }
}
