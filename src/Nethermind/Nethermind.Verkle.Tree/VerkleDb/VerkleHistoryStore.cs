// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;

namespace Nethermind.Verkle.Tree.VerkleDb;

public class VerkleHistoryStore
{
    private DiffLayer ForwardDiff { get; }
    private DiffLayer ReverseDiff { get; }

    public VerkleHistoryStore(IDbProvider dbProvider)
    {
        ForwardDiff = new DiffLayer(dbProvider.ForwardDiff, DiffType.Forward);
        ReverseDiff = new DiffLayer(dbProvider.ReverseDiff, DiffType.Reverse);
    }

    public void InsertDiff(long blockNumber, VerkleMemoryDb postState, VerkleMemoryDb preState)
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
