// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;

namespace Nethermind.Taiko.Rpc;

public sealed class PreBuiltTxList(TransactionForRpc[] transactions, ulong estimatedGasUsed, ulong bytesLength)
{
    [JsonPropertyName("TxList")]
    public TransactionForRpc[] TxList { get; } = transactions;

    [JsonPropertyName("EstimatedGasUsed")]
    [JsonConverter(typeof(ULongRawJsonConverter))]
    public ulong EstimatedGasUsed { get; } = estimatedGasUsed;

    [JsonPropertyName("BytesLength")]
    [JsonConverter(typeof(ULongRawJsonConverter))]
    public ulong BytesLength { get; } = bytesLength;
}
