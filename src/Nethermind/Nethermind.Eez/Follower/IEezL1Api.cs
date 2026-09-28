// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Follower;

/// <summary>The L1 reads the follower makes. A read the node refuses or cannot serve returns <see langword="null"/>.</summary>
public interface IEezL1Api
{
    Task<ulong?> GetChainId();

    Task<EezL1Block?> GetBlockByNumber(ulong number);

    Task<EezL1Block?> GetLatestBlock();

    Task<EezL1Block?> GetFinalizedBlock();

    /// <returns>The logs, or <see langword="null"/> when the node refuses the range.</returns>
    Task<EezL1Log[]?> GetLogs(Address address, Hash256 topic0, Hash256? topic1, ulong fromBlock, ulong toBlock);

    /// <summary>Looks a transaction up by its block hash and index, which every node serves, unlike a lookup by hash.</summary>
    Task<EezL1Transaction?> GetTransactionByBlockHashAndIndex(Hash256 blockHash, ulong index);
}

public readonly struct EezL1Block
{
    public Hash256 Hash { get; init; }
    public Hash256 ParentHash { get; init; }
    public ulong Number { get; init; }
    public ulong Timestamp { get; init; }
}

public readonly struct EezL1Log
{
    public Address Address { get; init; }
    public Hash256[] Topics { get; init; }
    public byte[] Data { get; init; }
    public ulong BlockNumber { get; init; }
    public Hash256 BlockHash { get; init; }
    public Hash256 TransactionHash { get; init; }
    public ulong TransactionIndex { get; init; }
    public ulong LogIndex { get; init; }
}

public readonly struct EezL1Transaction
{
    public Hash256 Hash { get; init; }
    public byte[] Input { get; init; }
}
