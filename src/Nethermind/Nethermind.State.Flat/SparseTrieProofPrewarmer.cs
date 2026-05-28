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
        sparseTrieChannel.Writer.TryWrite(new HashedStateUpdate
        {
            StorageUpdates = new() { [flatTree.AccountPathHash] = new() { [slotHash] = LeafUpdate.Touched() } },
            PreviousStorageRoots = new() { [flatTree.AccountPathHash] = flatTree.RootHash },
        });
        // Return false: the caller increments _outstandingWarmups on true and expects a
        // matching WarmUp* callback to decrement. We never invoke those callbacks (the
        // sparse trie task owns the enqueued work), so we must not contribute to the counter
        // or scope disposal will stall on the 1s WaitForOutstandingWarmups timeout.
        return false;
    }

    public bool PushAddressJob(ITrieWarmer.IAddressWarmer scope, Address? path, int sequenceId)
    {
        if (path is null) return false;

        Hash256 hashedAddress = Keccak.Compute(path.Bytes);
        sparseTrieChannel.Writer.TryWrite(new HashedStateUpdate
        {
            AccountUpdates = new() { [hashedAddress] = LeafUpdate.Touched() },
        });
        // See PushSlotJob for why this returns false.
        return false;
    }

    public void OnEnterScope() { }
    public void OnExitScope() { }
}
