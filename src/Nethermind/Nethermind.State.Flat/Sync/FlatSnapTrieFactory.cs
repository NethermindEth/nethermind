// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Sync;

/// <summary>
/// ISnapTrieFactory implementation for flat state storage.
/// Uses IPersistence to create reader/writeBatch per tree for proper resource management.
/// </summary>
public class FlatSnapTrieFactory(IPersistence persistence, ILogManager logManager) : ISnapTrieFactory
{
    public ISnapStateTree CreateStateTree()
    {
        var reader = persistence.CreateReader();
        var writeBatch = persistence.CreateWriteBatch(reader.CurrentState, reader.CurrentState, WriteFlags.DisableWAL);
        return new FlatSnapStateTree(reader, writeBatch, logManager);
    }

    public ISnapStorageTree CreateStorageTree(in ValueHash256 accountPath)
    {
        var reader = persistence.CreateReader();
        var writeBatch = persistence.CreateWriteBatch(reader.CurrentState, reader.CurrentState, WriteFlags.DisableWAL);
        return new FlatSnapStorageTree(reader, writeBatch, accountPath.ToCommitment(), logManager);
    }

    public Hash256? ResolveStorageRoot(byte[] nodeData)
    {
        using var reader = persistence.CreateReader();
        try
        {
            TreePath emptyTreePath = TreePath.Empty;
            TrieNode node = new(NodeType.Unknown, nodeData, isDirty: true);
            var resolver = new PersistenceTrieStoreAdapter(reader, null!);
            node.ResolveNode(resolver, emptyTreePath);
            node.ResolveKey(resolver, ref emptyTreePath);
            return node.Keccak;
        }
        catch
        {
            return null;
        }
    }
}
