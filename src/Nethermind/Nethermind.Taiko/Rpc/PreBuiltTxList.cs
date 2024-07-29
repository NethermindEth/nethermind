// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth;
using Nethermind.Serialization.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Taiko.Rpc;

public class PreBuiltTxList(TransactionForRpc[] transactions, long estimatedGasUsed, long bytesLength)
{
    public TransactionForRpc[] TxList { get; set; } = transactions;

    [JsonConverter(typeof(LongRawJsonConverter))]
    public long EstimatedGasUsed { get; set; } = estimatedGasUsed;

    [JsonConverter(typeof(LongRawJsonConverter))]
    public long BytesLength { get; set; } = bytesLength;
}
