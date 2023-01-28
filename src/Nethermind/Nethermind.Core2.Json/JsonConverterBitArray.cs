// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Json
{
    public class JsonConverterBitArray : JsonConverter<BitArray>
    {
        public override BitArray Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            byte[] bytes = reader.GetBytesFromPrefixedHex();
            return new BitArray(bytes.Select(x => x != 0).ToArray());
        }

        public override void Write(Utf8JsonWriter writer, BitArray value, JsonSerializerOptions options)
        {
            byte[] bytes = value.Cast<bool>().Select(x => x ? (byte)0x01 : (byte)0x00).ToArray();
            writer.WritePrefixedHexStringValue(bytes);
        }
    }
}
