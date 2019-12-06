﻿using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Containers.Json
{
    public class JsonConverterForkVersion : JsonConverter<ForkVersion>
    {
        public override ForkVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new ForkVersion(reader.GetBytesFromPrefixedHex());
        }

        public override void Write(Utf8JsonWriter writer, ForkVersion value, JsonSerializerOptions options)
        {
            writer.WritePrefixedHexStringValue(value.AsSpan());
        }
    }
}
