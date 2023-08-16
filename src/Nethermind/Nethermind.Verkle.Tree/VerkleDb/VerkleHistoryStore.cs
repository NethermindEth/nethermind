// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Verkle.Tree.VerkleDb;

public class VerkleHistoryStore
{
    private readonly ILogger _logger;
    private DiffLayer ForwardDiff { get; }
    private DiffLayer ReverseDiff { get; }

    public VerkleHistoryStore(IDbProvider dbProvider, ILogManager logManager)
    {
        ForwardDiff = new DiffLayer(dbProvider.ForwardDiff, DiffType.Forward);
        ReverseDiff = new DiffLayer(dbProvider.ReverseDiff, DiffType.Reverse);
        _logger = logManager?.GetClassLogger<VerkleHistoryStore>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public VerkleHistoryStore(IDb forwardDiff, IDb reverseDiff, ILogManager logManager)
    {
        ForwardDiff = new DiffLayer(forwardDiff, DiffType.Forward);
        ReverseDiff = new DiffLayer(reverseDiff, DiffType.Reverse);
        _logger = logManager?.GetClassLogger<VerkleHistoryStore>() ?? throw new ArgumentNullException(nameof(logManager));
    }

    public void InsertDiff(long blockNumber, VerkleMemoryDb postState, VerkleMemoryDb preState)
    {
        ForwardDiff.InsertDiff(blockNumber, postState);
        ReverseDiff.InsertDiff(blockNumber, preState);
    }

    public void InsertDiff(long blockNumber, ReadOnlyVerkleMemoryDb postState, VerkleMemoryDb preState)
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
}
