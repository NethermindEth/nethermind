// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.History.V1;

public class VerkleHistoryStore
{
    private readonly ILogger _logger;

    public VerkleHistoryStore(IDbProvider dbProvider, ILogManager logManager)
    {
        ForwardDiff = new DiffLayer(dbProvider.ForwardDiff, DiffType.Forward);
        ReverseDiff = new DiffLayer(dbProvider.ReverseDiff, DiffType.Reverse);
        _logger = logManager?.GetClassLogger<VerkleHistoryStore>() ??
                  throw new ArgumentNullException(nameof(logManager));
    }

    public VerkleHistoryStore(IDb forwardDiff, IDb reverseDiff, ILogManager logManager)
    {
        ForwardDiff = new DiffLayer(forwardDiff, DiffType.Forward);
        ReverseDiff = new DiffLayer(reverseDiff, DiffType.Reverse);
        _logger = logManager?.GetClassLogger<VerkleHistoryStore>() ??
                  throw new ArgumentNullException(nameof(logManager));
    }

    private DiffLayer ForwardDiff { get; }
    private DiffLayer ReverseDiff { get; }

    public void InsertDiff(long blockNumber, VerkleMemoryDb postState, VerkleMemoryDb preState)
    {
        ForwardDiff.InsertDiff(blockNumber, postState);
        ReverseDiff.InsertDiff(blockNumber, preState);
    }

    public void InsertDiff(long blockNumber, SortedVerkleMemoryDb postState, VerkleMemoryDb preState)
    {
        ForwardDiff.InsertDiff(blockNumber, postState);
        ReverseDiff.InsertDiff(blockNumber, preState);
    }

    public BatchChangeSet GetBatchDiff(long fromBlock, long toBlock)
    {
        VerkleMemoryDb diff = (fromBlock > toBlock) switch
        {
            true => ReverseDiff.MergeDiffs(fromBlock, toBlock),
            false => ForwardDiff.MergeDiffs(fromBlock, toBlock)
        };

        return new BatchChangeSet(fromBlock, toBlock, diff);
    }

    public VerkleMemoryDb GetReverseDiff(long fromBlock)
    {
        return ReverseDiff.FetchDiff(fromBlock);
    }

    public VerkleMemoryDb GetForwardDiff(long fromBlock)
    {
        return ForwardDiff.FetchDiff(fromBlock);
    }


    public byte[]? GetReverseDiffLeaf(long fromBlock, ReadOnlySpan<byte> key)
    {
        return ReverseDiff.GetLeaf(fromBlock, key);
    }

    public byte[]? GetForwardDiffLeaf(long fromBlock, ReadOnlySpan<byte> key)
    {
        return ForwardDiff.GetLeaf(fromBlock, key);
    }
}
