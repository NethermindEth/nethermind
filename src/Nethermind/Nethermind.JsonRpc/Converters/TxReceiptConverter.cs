// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Converters
{
    public class TxReceiptConverter : JsonConverter<TxReceipt>
    {
        public override void WriteJson(JsonWriter writer, TxReceipt value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, new ReceiptForRpc(value.TxHash!, value, new(UInt256.Zero)));
        }

        public override TxReceipt ReadJson(JsonReader reader, Type objectType, TxReceipt existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return serializer.Deserialize<ReceiptForRpc>(reader)?.ToReceipt() ?? existingValue;
        }
    }
}
