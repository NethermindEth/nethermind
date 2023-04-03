// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.Serialization.Json
{
    public class StorageCellIndexConverter : JsonConverter<UInt256>
    {
        public override void WriteJson(JsonWriter writer, UInt256 value, JsonSerializer serializer)
        {
            writer.WriteValue(value.ToHexString(false));
        }

        public override UInt256 ReadJson(JsonReader reader, Type objectType, UInt256 existingValue, bool hasExistingValue, JsonSerializer serializer) =>
            UInt256Converter.ReaderJson(reader);
    }
}
