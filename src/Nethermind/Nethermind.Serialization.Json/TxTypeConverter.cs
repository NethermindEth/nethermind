// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core;

namespace Nethermind.Serialization.Json
{

    public class TxTypeConverter : JsonConverter<TxType>
    {
        public override TxType Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            return (TxType)Convert.ToByte(reader.GetString(), 16);
        }

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
            ByteArrayConverter.Convert(writer, MemoryMarshal.CreateReadOnlySpan(ref byteValue, 1), skipLeadingZeros: true);
        }
    }
}
