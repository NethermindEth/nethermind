// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// ISnapTrieFactory implementation for flat state storage.
/// Uses IPersistence.IWriteBatch directly for efficient bulk imports during snap sync.
/// </summary>
public class FlatSnapTrieFactory(IPersistence.IWriteBatch writeBatch, ILogManager logManager) : ISnapTrieFactory
{
    private readonly PersistenceTrieStoreAdapter _stateTrieStore = new(writeBatch);

    public ISnapStateTree CreateStateTree() =>
        new FlatSnapStateTree(new StateTree(_stateTrieStore, logManager));

    public ISnapStorageTree CreateStorageTree(in ValueHash256 accountPath) =>
        new FlatSnapStorageTree(new StorageTree(_stateTrieStore.GetStorageTrieStore(accountPath.ToCommitment()), logManager));

    public Hash256? ResolveStorageRoot(byte[] nodeData)
    {
        try
        {
            TreePath emptyTreePath = TreePath.Empty;
            TrieNode node = new(NodeType.Unknown, nodeData, isDirty: true);
            node.ResolveNode(_stateTrieStore, emptyTreePath);
            node.ResolveKey(_stateTrieStore, ref emptyTreePath);
            return node.Keccak;
        }
        catch
        {
            return null;
        }
    }
}
