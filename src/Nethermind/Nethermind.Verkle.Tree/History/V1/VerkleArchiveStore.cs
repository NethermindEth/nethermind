// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections.EliasFano;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Verkle.Tree.History.V2;
using Nethermind.Verkle.Tree.TreeStore;
using Nethermind.Verkle.Tree.Utils;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.History.V1;

public class VerkleArchiveStore
{
    private readonly HistoryOfAccounts _historyOfAccounts;

    private readonly IVerkleTreeStore _stateStore;

    public VerkleArchiveStore(IVerkleTreeStore stateStore, IDbProvider dbProvider, ILogManager logManager)
    {
        _stateStore = stateStore;
        _historyOfAccounts = new HistoryOfAccounts(dbProvider.HistoryOfAccounts);
        _stateStore.InsertBatchCompletedV1 += OnPersistNewBlock;
        History = new VerkleHistoryStore(dbProvider, logManager);
    }

    public int BlockChunks
    {
        get => _historyOfAccounts.BlocksChunks;
        set => _historyOfAccounts.BlocksChunks = value;
    }

    private VerkleHistoryStore History { get; }

    private void OnPersistNewBlock(object? sender, InsertBatchCompletedV1 insertBatchCompleted)
    {
        Console.WriteLine(
            $"Inserting after commit: BN:{insertBatchCompleted.BlockNumber} FD:{insertBatchCompleted.ForwardDiff.LeafTable.Count} RD:{insertBatchCompleted.ReverseDiff.LeafTable.Count}");
        var blockNumber = insertBatchCompleted.BlockNumber;
        VerkleMemoryDb revDiff = insertBatchCompleted.ReverseDiff;
        SortedVerkleMemoryDb forwardDiff = insertBatchCompleted.ForwardDiff;
        History.InsertDiff(blockNumber, forwardDiff, revDiff);

        foreach (KeyValuePair<byte[], byte[]?> keyVal in forwardDiff.LeafTable)
            _historyOfAccounts.AppendHistoryBlockNumberForKey(new Hash256(keyVal.Key), (ulong)blockNumber);
    }

    public byte[]? GetLeaf(ReadOnlySpan<byte> key, Hash256 rootHash)
    {
        var blockNumber = _stateStore.GetBlockNumber(rootHash);
        EliasFano? requiredShard = _historyOfAccounts.GetAppropriateShard(new Hash256(key.ToArray()), blockNumber);
        if (requiredShard is null) return null;

        var requiredBlock = requiredShard.Value.Predecessor(blockNumber);

        VerkleMemoryDb diff = History.GetForwardDiff((long)requiredBlock!.Value);

        diff.GetLeaf(key, out var value);
        return value;
    }

    // This generates and returns a batchForwardDiff, that can be used to move the full state from fromBlock to toBlock.
    // for this fromBlock < toBlock - move forward in time
    public bool GetForwardMergedDiff(long fromBlock, long toBlock, [MaybeNullWhen(false)] out VerkleMemoryDb diff)
    {
        diff = History.GetBatchDiff(fromBlock, toBlock).DiffLayer;
        return true;
    }

    // This generates and returns a batchForwardDiff, that can be used to move the full state from fromBlock to toBlock.
    // for this fromBlock > toBlock - move back in time
    public bool GetReverseMergedDiff(long fromBlock, long toBlock, [MaybeNullWhen(false)] out VerkleMemoryDb diff)
    {
        diff = History.GetBatchDiff(fromBlock, toBlock).DiffLayer;
        return true;
    }
}
