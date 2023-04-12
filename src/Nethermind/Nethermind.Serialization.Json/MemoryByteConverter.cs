// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json;

public class MemoryByteConverter : JsonConverter<Memory<byte>>
{
    public override void WriteJson(JsonWriter writer, Memory<byte> value, JsonSerializer serializer)
    {
        if (value.IsEmpty)
        {
            writer.WriteNull();
        }
        else
        {
            writer.WriteValue(Bytes.ByteArrayToHexViaLookup32Safe(value, true));
        }
    }

    public override Memory<byte> ReadJson(JsonReader reader, Type objectType, Memory<byte> existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        string s = (string)reader.Value;
        return Bytes.FromHexString(s);
    }
}
