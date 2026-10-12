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
    public override TxReceipt? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => TypeInfoJsonSerializer.Deserialize<ReceiptForRpc>(ref reader, options)?.ToReceipt();

    public override void Write(Utf8JsonWriter writer, TxReceipt value, JsonSerializerOptions options)
    {
        NumberConversion previousValue = ForcedNumberConversion.Value;
        ForcedNumberConversion.Value = NumberConversion.Hex;
        try
        {
            writer.WriteStartObject();
            ReceiptForRpc receipt = new(value.TxHash!, value, 0, default);
            if (receipt.Type != TxType.Legacy)
            {
                writer.WritePropertyName("type");
                TypeInfoJsonSerializer.Serialize(writer, receipt.Type, options);
            }
            // EIP-658: a receipt carries either a post-state root (pre-Byzantium) or a status code, never both.
            if (receipt.Root is not null)
            {
                writer.WritePropertyName("root");
                TypeInfoJsonSerializer.Serialize(writer, receipt.Root, options);
            }
            else
            {
                writer.WritePropertyName("status");
                TypeInfoJsonSerializer.Serialize(writer, receipt.Status, options);
            }

            writer.WritePropertyName("cumulativeGasUsed");
            TypeInfoJsonSerializer.Serialize(writer, receipt.CumulativeGasUsed, options);
            writer.WritePropertyName("effectiveGasPrice");
            TypeInfoJsonSerializer.Serialize(writer, receipt.EffectiveGasPrice, options);
            writer.WritePropertyName("logsBloom");
            TypeInfoJsonSerializer.Serialize(writer, receipt.LogsBloom, options);
            writer.WritePropertyName("logs");
            if (receipt.Logs is ReceiptLogsForRpc { Count: > 0 } logs)
            {
                logs.Write(writer, options);
            }
            else
            {
                writer.WriteNullValue();
            }
            // EIP-8141: emitted only for frame transactions, so other receipts keep their existing shape.
            if (receipt.Type == TxType.FrameTx)
            {
                writer.WritePropertyName("payer");
                TypeInfoJsonSerializer.Serialize(writer, receipt.Payer, options);
                writer.WritePropertyName("frameReceipts");
                TypeInfoJsonSerializer.Serialize(writer, receipt.FrameReceipts, options);
            }
            writer.WritePropertyName("transactionHash");
            TypeInfoJsonSerializer.Serialize(writer, receipt.TransactionHash, options);
            writer.WritePropertyName("contractAddress");
            TypeInfoJsonSerializer.Serialize(writer, receipt.ContractAddress, options);
            writer.WritePropertyName("gasUsed");
            TypeInfoJsonSerializer.Serialize(writer, receipt.GasUsed, options);
            // Diagnostic-only EIP-7778 gas breakdown.
            if (value.BlockGasUsed > 0)
            {
                writer.WritePropertyName("blockGasUsed");
                TypeInfoJsonSerializer.Serialize(writer, value.BlockGasUsed, options);
            }
            // Diagnostic-only EIP-8037 gas breakdown.
            if (value.StorageGasUsed > 0 || value.ExecutionGasUsed > 0)
            {
                writer.WritePropertyName("executionGasUsed");
                TypeInfoJsonSerializer.Serialize(writer, value.ExecutionGasUsed, options);
                writer.WritePropertyName("storageGasUsed");
                TypeInfoJsonSerializer.Serialize(writer, value.StorageGasUsed, options);
            }
            writer.WritePropertyName("blockHash");
            TypeInfoJsonSerializer.Serialize(writer, receipt.BlockHash ?? Hash256.Zero, options);

            writer.WritePropertyName("transactionIndex");
            TypeInfoJsonSerializer.Serialize(writer, UInt256.Parse(receipt.TransactionIndex.ToString(), NumberStyles.Integer), options);

            writer.WriteEndObject();
        }
        finally
        {
            ForcedNumberConversion.Value = previousValue;
        }
    }
}
