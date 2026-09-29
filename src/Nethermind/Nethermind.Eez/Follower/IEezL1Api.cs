// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Follower;

/// <summary>The L1 reads the follower makes. A read the node refuses or cannot serve returns <see langword="null"/>.</summary>
public interface IEezL1Api
{
    Task<ulong?> GetChainId(CancellationToken token);

    Task<EezL1Block?> GetBlockByNumber(ulong number, CancellationToken token);

    Task<EezL1Block?> GetLatestBlock(CancellationToken token);

    Task<EezL1Block?> GetFinalizedBlock(CancellationToken token);

    /// <returns>The logs, or <see langword="null"/> when the node refuses the range.</returns>
    Task<EezL1Log[]?> GetLogs(Address address, Hash256 topic0, Hash256? topic1, ulong fromBlock, ulong toBlock, CancellationToken token);

    /// <summary>Looks a transaction up by its block hash and index, which every node serves, unlike a lookup by hash.</summary>
    Task<EezL1Transaction?> GetTransactionByBlockHashAndIndex(Hash256 blockHash, ulong index, CancellationToken token);

    /// <returns>What a view call on the latest block returns, or <see langword="null"/> when the node refuses it.</returns>
    Task<byte[]?> Call(Address to, byte[] data, CancellationToken token);
}

public static class EezL1ApiExtensions
{
    /// <summary>Reads the logs of a range, halving it while the node refuses it.</summary>
    /// <exception cref="L1SourceIncompleteException">The node refuses the logs of a single block.</exception>
    public static async Task<List<EezL1Log>> GetAllLogs(this IEezL1Api l1, Address address, Hash256 topic0, Hash256? topic1, ulong fromBlock, ulong toBlock,
        CancellationToken token)
    {
        List<EezL1Log> logs = [];
        Stack<(ulong From, ulong To)> ranges = new();
        ranges.Push((fromBlock, toBlock));
        while (ranges.TryPop(out (ulong From, ulong To) range))
        {
            EezL1Log[]? chunk = await l1.GetLogs(address, topic0, topic1, range.From, range.To, token);
            if (chunk is not null)
            {
                logs.AddRange(chunk);
                continue;
            }

            if (range.From == range.To)
            {
                throw new L1SourceIncompleteException(range.From, $"L1 refuses the logs of block {range.From}.");
            }

            ulong middle = range.From + (range.To - range.From) / 2;
            ranges.Push((middle + 1, range.To));
            ranges.Push((range.From, middle));
        }

        return logs;
    }
}

public readonly struct EezL1Block
{
    public Hash256 Hash { get; init; }
    public Hash256 ParentHash { get; init; }
    public ulong Number { get; init; }
    public ulong Timestamp { get; init; }
    public UInt256? BaseFeePerGas { get; init; }

    /// <summary>The hashes of the block's transactions.</summary>
    public Hash256[]? Transactions { get; init; }
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
