// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.TraceStore.Ots;

/// <summary>A descending page of transactions and corresponding receipts, never split within a block.</summary>
public sealed class OtsSearchResult
{
    public List<TransactionForRpc> Txs { get; } = [];
    public List<OtsReceipt> Receipts { get; } = [];
    public bool FirstPage { get; set; }
    public bool LastPage { get; set; }
}

/// <summary>An Ethereum receipt with the decimal block timestamp required by OTS.</summary>
public sealed class OtsReceipt(Hash256 hash, TxReceipt receipt, ulong timestamp, TxGasInfo gasInfo, int logIndexStart)
    : ReceiptForRpc(hash, receipt, timestamp, gasInfo, logIndexStart)
{
    [JsonConverter(typeof(ULongRawJsonConverter))]
    public ulong Timestamp { get; } = timestamp;
}

/// <summary>A call-tree entry. A value does not imply that the call or its ancestors succeeded.</summary>
public sealed class OtsTraceEntry
{
    public required string Type { get; init; }
    public int Depth { get; init; }
    public required Address From { get; init; }
    public required Address To { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? Value { get; init; }

    public required byte[] Input { get; init; }
    public required byte[] Output { get; init; }
}
