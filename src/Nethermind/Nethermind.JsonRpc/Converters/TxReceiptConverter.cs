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
    public override TxReceipt? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => JsonSerializer.Deserialize<ReceiptForRpc>(ref reader, options)?.ToReceipt();

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
                JsonSerializer.Serialize(writer, receipt.Type, options);
            }
            writer.WritePropertyName("root");
            ByteArrayConverter.Convert(writer, (receipt.Root ?? Keccak.Zero).Bytes);
            writer.WritePropertyName("status");
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
            // Diagnostic-only EIP-7778 gas breakdown.
            if (value.BlockGasUsed > 0)
            {
                writer.WritePropertyName("blockGasUsed");
                JsonSerializer.Serialize(writer, value.BlockGasUsed, options);
            }
            // Diagnostic-only EIP-8037 gas breakdown.
            if (value.StorageGasUsed > 0 || value.ExecutionGasUsed > 0)
            {
                writer.WritePropertyName("executionGasUsed");
                JsonSerializer.Serialize(writer, value.ExecutionGasUsed, options);
                writer.WritePropertyName("storageGasUsed");
                JsonSerializer.Serialize(writer, value.StorageGasUsed, options);
            }
            writer.WritePropertyName("blockHash");
            JsonSerializer.Serialize(writer, receipt.BlockHash ?? Hash256.Zero, options);

            writer.WritePropertyName("transactionIndex");
            JsonSerializer.Serialize(writer, UInt256.Parse(receipt.TransactionIndex.ToString(), NumberStyles.Integer), options);

            writer.WriteEndObject();
        }
        finally
        {
            ForcedNumberConversion.Value = previousValue;
        }
    }
}
