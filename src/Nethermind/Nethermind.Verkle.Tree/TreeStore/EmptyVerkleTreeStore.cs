// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.TreeNodes;
using Nethermind.Verkle.Tree.Utils;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.TreeStore;

public class EmptyVerkleTreeStore: IVerkleTreeStore
{
    public IEnumerable<KeyValuePair<byte[], byte[]>> GetLeafRangeIterator(byte[] fromRange, byte[] toRange, Hash256 stateRoot)
    {
        yield break;
    }

    public IEnumerable<PathWithSubTree> GetLeafRangeIterator(Stem fromRange, Stem toRange, Hash256 stateRoot, long bytes)
    {
        yield break;
    }

    public void InsertRootNodeAfterSyncCompletion(byte[] rootHash, long blockNumber)
    {
    }

    public void InsertSyncBatch(long blockNumber, VerkleMemoryDb batch)
    {
    }

    public Hash256 StateRoot => Hash256.Zero;
    public bool HasStateForBlock(Hash256 stateRoot)
    {
        return false;
    }

    public bool MoveToStateRoot(Hash256 stateRoot)
    {
        return false;
    }

    public byte[]? GetLeaf(ReadOnlySpan<byte> key, Hash256? stateRoot = null)
    {
        return null;
    }

    public InternalNode? GetInternalNode(ReadOnlySpan<byte> key, Hash256? stateRoot = null)
    {
        return null;
    }

    public void InsertBatch(long blockNumber, VerkleMemoryDb memDb, bool skipRoot = false)
    {
    }

    public IReadOnlyVerkleTreeStore AsReadOnly(VerkleMemoryDb tempKeyValueStore)
    {
        throw new NotImplementedException();
    }

    public ulong GetBlockNumber(Hash256 rootHash)
    {
        throw new NotImplementedException();
    }

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
}
