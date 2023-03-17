// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Serialization.Json
{
    using Newtonsoft.Json;

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

namespace Nethermind.Serialization.Json
{
    using System.Runtime.InteropServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    public class TxTypeJsonConverter : JsonConverter<TxType>
    {
        public override TxType Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => throw new NotImplementedException();

        public override void Write(
            Utf8JsonWriter writer,
            TxType txTypeValue,
            JsonSerializerOptions options)
        {
            if (txTypeValue == TxType.Legacy)
            {
                writer.WriteRawValue("\"0x0\""u8, skipInputValidation: true);
                return;
            }

            byte byteValue = (byte)txTypeValue;
            ByteArrayJsonConverter.Convert(writer, MemoryMarshal.CreateReadOnlySpan(ref byteValue, 1), skipLeadingZeros: true);
        }
    }
}
