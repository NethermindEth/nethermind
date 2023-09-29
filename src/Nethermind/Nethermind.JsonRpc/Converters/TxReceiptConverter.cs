// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Converters;

public class TxReceiptConverter : JsonConverter<TxReceipt>
{
    public override TxReceipt? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return JsonSerializer.Deserialize<ReceiptForRpc>(ref reader, options)?.ToReceipt();
    }

    public override void Write(Utf8JsonWriter writer, TxReceipt value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new ReceiptForRpc(value.TxHash!, value, UInt256.Zero), options);
    }
}
