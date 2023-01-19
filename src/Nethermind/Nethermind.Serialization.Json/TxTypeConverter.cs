// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class TxTypeConverter : JsonConverter<TxType>
    {
        public override void WriteJson(JsonWriter writer, TxType txTypeValue, JsonSerializer serializer)
        {
            byte byteValue = (byte)txTypeValue;
            writer.WriteValue(string.Concat("0x", byteValue.ToString("X")));
        }

        public override TxType ReadJson(JsonReader reader, Type objectType, TxType existingValue, bool hasExistingValue,
            JsonSerializer serializer)
        {
            string s = (string)reader.Value;
            return (TxType)Convert.ToByte(s, 16);
        }
    }
}
