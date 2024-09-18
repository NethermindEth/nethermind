// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Facade.Eth;
using Nethermind.Serialization.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Taiko.Rpc;

public sealed class PreBuiltTxList(TransactionForRpc[] transactions, ulong estimatedGasUsed, ulong bytesLength)
{
    public TransactionForRpc[] TxList { get; } = transactions;

    [JsonConverter(typeof(LongRawJsonConverter))]
    public ulong EstimatedGasUsed { get; } = estimatedGasUsed;

    [JsonConverter(typeof(LongRawJsonConverter))]
    public ulong BytesLength { get; } = bytesLength;
}
