// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using Nethermind.Core;
using Nethermind.JsonRpc.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Converters;

public class TxReceiptConverter : JsonConverter<TxReceipt>
{
    public override TxReceipt? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return JsonSerializer.Deserialize<ReceiptForRpc>(ref reader, options)?.ToReceipt();
    }

    public override void Write(Utf8JsonWriter writer, TxReceipt value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        var receipt = new ReceiptForRpc(value.TxHash!, value, 0, default);
        if (receipt.Type != TxType.Legacy)
        {
            writer.WritePropertyName("type");
            JsonSerializer.Serialize(writer, receipt.Type, options);
        }
        writer.WritePropertyName("root");
        ByteArrayConverter.Convert(writer, (receipt.Root ?? Keccak.Zero).Bytes);
        writer.WritePropertyName("status");
        ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Hex;
        JsonSerializer.Serialize(writer, receipt.Status, options);

        writer.WritePropertyName("cumulativeGasUsed");
        JsonSerializer.Serialize(writer, receipt.CumulativeGasUsed, options);
        writer.WritePropertyName("effectiveGasPrice");
        JsonSerializer.Serialize(writer, receipt.EffectiveGasPrice, options);
        writer.WritePropertyName("logsBloom");
        JsonSerializer.Serialize(writer, receipt.LogsBloom, options);
        writer.WritePropertyName("logs");
        JsonSerializer.Serialize(writer, receipt.Logs.Length == 0 ? null : receipt.Logs, options);
        writer.WritePropertyName("transactionHash");
        JsonSerializer.Serialize(writer, receipt.TransactionHash, options);
        writer.WritePropertyName("contractAddress");
        JsonSerializer.Serialize(writer, receipt.ContractAddress, options);
        writer.WritePropertyName("gasUsed");
        JsonSerializer.Serialize(writer, receipt.GasUsed, options);
        writer.WritePropertyName("blockHash");
        JsonSerializer.Serialize(writer, receipt.BlockHash ?? Hash256.Zero, options);

        writer.WritePropertyName("transactionIndex");
        JsonSerializer.Serialize(writer, UInt256.Parse(receipt.TransactionIndex.ToString(), NumberStyles.Integer), options);

        writer.WriteEndObject();
    }
}
