// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Evm.T8n.JsonTypes;

/// <summary>A transaction receipt in the JSON layout of geth's <c>types.Receipt</c>.</summary>
public class T8nReceipt
{
    public TxType? Type { get; init; }
    public required byte[] Root { get; init; }
    public ulong Status { get; init; }
    public ulong CumulativeGasUsed { get; init; }
    public required Bloom LogsBloom { get; init; }
    public required T8nLog[] Logs { get; init; }
    public required Hash256 TransactionHash { get; init; }
    public required Address ContractAddress { get; init; }
    public ulong GasUsed { get; init; }

    /// <remarks>geth's <c>t8n</c> leaves it unset, so it is always <c>null</c>.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public UInt256? EffectiveGasPrice { get; init; }

    public ulong? BlobGasUsed { get; init; }
    public UInt256? BlobGasPrice { get; init; }
    public required Hash256 BlockHash { get; init; }
    public ulong BlockNumber { get; init; }
    public ulong TransactionIndex { get; init; }
}

/// <summary>A log entry in the JSON layout of geth's <c>types.Log</c>.</summary>
public class T8nLog
{
    public required Address Address { get; init; }
    public required Hash256[] Topics { get; init; }
    public required byte[] Data { get; init; }
    public ulong BlockNumber { get; init; }
    public required Hash256 TransactionHash { get; init; }
    public ulong TransactionIndex { get; init; }
    public required Hash256 BlockHash { get; init; }
    public ulong BlockTimestamp { get; init; }
    public ulong LogIndex { get; init; }
    public bool Removed { get; init; }
}
