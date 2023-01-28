// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Json
{
    public class JsonConverterSlot : JsonConverter<Slot>
    {
        public override Slot Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new Slot(reader.GetUInt64());
        }

        public override void Write(Utf8JsonWriter writer, Slot value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }
}
