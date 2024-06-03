// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Collections.EliasFano;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Verkle.Tree.TreeStore;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Verkle.Tree.History.V2;

public class VerkleArchiveStore
{
    private readonly HistoryOfAccounts _historyOfAccounts;

    private readonly IVerkleTreeStore _stateStore;

    public VerkleArchiveStore(IVerkleTreeStore stateStore, IDbProvider dbProvider, ILogManager logManager)
    {
        _stateStore = stateStore;
        _historyOfAccounts = new HistoryOfAccounts(dbProvider.HistoryOfAccounts);
        _stateStore.InsertBatchCompletedV2 += OnPersistNewBlock;
        ChangeSet = new LeafChangeSet(dbProvider, logManager);
    }

    public int BlockChunks
    {
        get => _historyOfAccounts.BlocksChunks;
        set => _historyOfAccounts.BlocksChunks = value;
    }

    private ILeafChangeSet ChangeSet { get; }

    private void OnPersistNewBlock(object? sender, InsertBatchCompletedV2 insertBatchCompleted)
    {
        var blockNumber = insertBatchCompleted.BlockNumber;
        ChangeSet.InsertDiff(blockNumber, insertBatchCompleted.LeafTable);
        foreach (KeyValuePair<byte[], byte[]?> keyVal in insertBatchCompleted.LeafTable)
            _historyOfAccounts.AppendHistoryBlockNumberForKey(new Hash256(keyVal.Key), (ulong)blockNumber);
    }

    public byte[]? GetLeaf(ReadOnlySpan<byte> key, Hash256 rootHash)
    {
        var blockNumber = _stateStore.GetBlockNumber(rootHash);
        EliasFano? requiredShard = _historyOfAccounts.GetAppropriateShard(new Hash256(key.ToArray()), blockNumber + 1);
        if (requiredShard is null) return _stateStore.GetLeaf(key);
        Console.WriteLine($"RequiredShard:{string.Join(",", requiredShard.Value.GetEnumerator(0).ToArray())}");
        var requiredBlock = requiredShard.Value.Successor(blockNumber + 1);
        return requiredBlock is null ? _stateStore.GetLeaf(key) : ChangeSet.GetLeaf((long)requiredBlock!.Value, key);
    }
}
