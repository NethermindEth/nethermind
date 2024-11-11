// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Evm.T8n.JsonConverters;

// required to serialize in geth t8n format
public class ReceiptJsonConverter : JsonConverter<TxReceipt>
{
    private readonly EthereumJsonSerializer _ethereumJsonSerializer = new();

    public override TxReceipt Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return _ethereumJsonSerializer.Deserialize<TxReceipt>(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, TxReceipt receipt, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (receipt.TxType != TxType.Legacy)
        {
            writer.WritePropertyName("type");
            JsonSerializer.Serialize(writer, receipt.TxType, options);
        }
        writer.WritePropertyName("root");
        ByteArrayConverter.Convert(writer, (receipt.PostTransactionState ?? Keccak.Zero).Bytes);

        writer.WritePropertyName("status");
        ForcedNumberConversion.ForcedConversion.Value = NumberConversion.Hex;
        JsonSerializer.Serialize(writer, (long) receipt.StatusCode, options);

        writer.WritePropertyName("cumulativeGasUsed");
        JsonSerializer.Serialize(writer, receipt.GasUsedTotal, options);
        writer.WritePropertyName("logsBloom");
        JsonSerializer.Serialize(writer, receipt.Bloom, options);
        writer.WriteNull("logs");
        writer.WriteNull("effectiveGasPrice");
        writer.WritePropertyName("transactionHash");
        JsonSerializer.Serialize(writer, receipt.TxHash, options);
        writer.WritePropertyName("contractAddress");
        JsonSerializer.Serialize(writer, receipt.ContractAddress ?? Address.Zero, options);
        writer.WritePropertyName("gasUsed");
        JsonSerializer.Serialize(writer, receipt.GasUsed, options);
        writer.WritePropertyName("blockHash");
        JsonSerializer.Serialize(writer, receipt.BlockHash ?? Keccak.Zero, options);

        writer.WritePropertyName("transactionIndex");
        JsonSerializer.Serialize(writer, UInt256.Parse(receipt.Index.ToString(), NumberStyles.Integer), options);

        writer.WriteEndObject();
    }
}
