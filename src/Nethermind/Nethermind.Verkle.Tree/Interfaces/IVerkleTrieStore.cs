// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Verkle;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.Nodes;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.Verkle.Tree.Interfaces;

public interface IVerkleTrieStore: IStoreWithReorgBoundary, IVerkleSyncTireStore
{
    VerkleCommitment StateRoot { get; }
    VerkleCommitment GetStateRoot();
    bool MoveToStateRoot(VerkleCommitment stateRoot);

    byte[]? GetLeaf(ReadOnlySpan<byte> key);
    InternalNode? GetInternalNode(ReadOnlySpan<byte> key);

    void Flush(long blockNumber, VerkleMemoryDb memDb);
    void Reset();

    void ReverseState();
    void ApplyDiffLayer(BatchChangeSet changeSet);
    bool GetForwardMergedDiff(long fromBlock, long toBlock, [MaybeNullWhen(false)] out VerkleMemoryDb diff);
    bool GetReverseMergedDiff(long fromBlock, long toBlock, [MaybeNullWhen(false)] out VerkleMemoryDb diff);

    ReadOnlyVerkleStateStore AsReadOnly(VerkleMemoryDb keyValueStore);
}
