// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.TreeNodes;
using Nethermind.Verkle.Tree.Utils;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.TreeStore;

public class ReadOnlyVerkleStateStore(IVerkleTreeStore verkleStateStore, VerkleMemoryDb keyValueStore)
    : IReadOnlyVerkleTreeStore
{
    private static Span<byte> RootNodeKey => Array.Empty<byte>();

    public Hash256 StateRoot
    {
        get
        {
            keyValueStore.GetInternalNode(RootNodeKey, out InternalNode? value);
            return value is null ? _stateRoot : new Hash256(value.Bytes);
        }
    }

    private Hash256 _stateRoot;

    public byte[]? GetLeaf(ReadOnlySpan<byte> key, Hash256? stateRoot = null)
    {
        return keyValueStore.GetLeaf(key, out var value)
            ? value
            : verkleStateStore.GetLeaf(key, stateRoot);
    }

    public InternalNode? GetInternalNode(ReadOnlySpan<byte> key, Hash256? stateRoot = null)
    {
        return keyValueStore.GetInternalNode(key, out InternalNode? value)
            ? value
            : verkleStateStore.GetInternalNode(key, stateRoot);
    }

    public void InsertBatch(long blockNumber, VerkleMemoryDb batch, bool skipRoot)
    {
    }

    public bool HasStateForBlock(Hash256 stateRoot)
    {
        return verkleStateStore.HasStateForBlock(stateRoot);
    }

    public bool MoveToStateRoot(Hash256 stateRoot)
    {
        if (!HasStateForBlock(stateRoot)) return false;
        keyValueStore.LeafTable.Clear();
        keyValueStore.InternalTable.Clear();
        _stateRoot = stateRoot;
        return true;
    }

    public IReadOnlyVerkleTreeStore AsReadOnly(VerkleMemoryDb tempKeyValueStore)
    {
        return new ReadOnlyVerkleStateStore(verkleStateStore, tempKeyValueStore);
    }

    public ulong GetBlockNumber(Hash256 rootHash)
    {
        return verkleStateStore.GetBlockNumber(rootHash);
    }

    public void InsertRootNodeAfterSyncCompletion(byte[] rootHash, long blockNumber)
    {
        throw new NotImplementedException();
    }

    public void InsertSyncBatch(long blockNumber, VerkleMemoryDb batch) { }

    public event EventHandler<InsertBatchCompletedV1>? InsertBatchCompletedV1
    {
        add { }
        remove { }
    }
    public event EventHandler<InsertBatchCompletedV2>? InsertBatchCompletedV2
    {
        add { }
        remove { }
    }
    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add { }
        remove { }
    }
    public IEnumerable<KeyValuePair<byte[], byte[]>> GetLeafRangeIterator(byte[] fromRange, byte[] toRange,
        Hash256 stateRoot)
    {
        return verkleStateStore.GetLeafRangeIterator(fromRange, toRange, stateRoot);
    }

    public IEnumerable<PathWithSubTree> GetLeafRangeIterator(Stem fromRange, Stem toRange, Hash256 stateRoot,
        long bytes)
    {
        return verkleStateStore.GetLeafRangeIterator(fromRange, toRange, stateRoot, bytes);
    }
}
