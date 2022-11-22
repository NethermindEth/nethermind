// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class Bytes32Converter : JsonConverter<byte[]>
    {
        public override void WriteJson(JsonWriter writer, byte[] value, JsonSerializer serializer)
        {
            writer.WriteValue(string.Concat("0x", value.ToHexString(false).PadLeft(64, '0')));
        }

        public override byte[] ReadJson(
            JsonReader reader,
            Type objectType,
            byte[] existingValue,
            bool hasExistingValue,
            JsonSerializer serializer)
        {
            string s = (string)reader.Value;
            if (s is null)
            {
                return null;
            }

            return Bytes.FromHexString(s);
        }
    }
}
