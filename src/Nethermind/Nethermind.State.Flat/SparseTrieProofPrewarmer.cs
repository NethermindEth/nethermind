// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat;

/// <summary>
/// M5: Replaces <see cref="TrieWarmer"/> with targeted proof prefetching.
/// Implements <see cref="ITrieWarmer"/> (the queue interface). Instead of walking trie paths
/// node-by-node, dispatches proof prefetch requests to the sparse trie task's channel.
/// The IAddressWarmer/IStorageWarmer callbacks on scope/storage-tree are never invoked.
/// </summary>
public sealed class SparseTrieProofPrewarmer(Channel<HashedStateUpdate> sparseTrieChannel) : ITrieWarmer
{
    public bool PushSlotJob(ITrieWarmer.IStorageWarmer storageWarmer, in UInt256? index, int sequenceId)
    {
        if (index is null) return false;
        if (storageWarmer is not FlatStorageTree flatTree) return false;

        ValueHash256 slotKey = default;
        Nethermind.State.StorageTree.ComputeKeyWithLookup(index.Value, ref slotKey);
        Hash256 slotHash = slotKey.ToCommitment();
        // IMPORTANT: only return true when actually enqueued. The caller (FlatStorageTree.HintSet)
        // increments _outstandingWarmups based on our return — if we return true on a full channel,
        // the increment is never matched and scope disposal stalls.
        return sparseTrieChannel.Writer.TryWrite(new HashedStateUpdate
        {
            StorageUpdates = new() { [flatTree.AccountPathHash] = new() { [slotHash] = LeafUpdate.Touched() } },
            PreviousStorageRoots = new() { [flatTree.AccountPathHash] = flatTree.RootHash },
        });
    }

    public bool PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId)
    {
        if (path is null) return false;

        Hash256 hashedAddress = Keccak.Compute(path.Bytes);
        return sparseTrieChannel.Writer.TryWrite(new HashedStateUpdate
        {
            AccountUpdates = new() { [hashedAddress] = LeafUpdate.Touched() },
        });
    }

    public void OnEnterScope() { }
    public void OnExitScope() { }
}
