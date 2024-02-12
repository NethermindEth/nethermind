// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.TreeNodes;
using Nethermind.Verkle.Tree.Utils;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.TreeStore;

public interface IVerkleTreeStore : IStoreWithReorgBoundary, IVerkleSyncTreeStore
{
    Hash256 StateRoot { get; }

    bool HasStateForBlock(Hash256 stateRoot);
    bool MoveToStateRoot(Hash256 stateRoot);

    byte[]? GetLeaf(ReadOnlySpan<byte> key, Hash256? stateRoot = null);
    InternalNode? GetInternalNode(ReadOnlySpan<byte> key, Hash256? stateRoot = null);

    void InsertBatch(long blockNumber, VerkleMemoryDb memDb, bool skipRoot = false);

    IReadOnlyVerkleTreeStore AsReadOnly(VerkleMemoryDb tempKeyValueStore);

    ulong GetBlockNumber(Hash256 rootHash);

    public event EventHandler<InsertBatchCompletedV1>? InsertBatchCompletedV1;

    public event EventHandler<InsertBatchCompletedV2>? InsertBatchCompletedV2;
}
