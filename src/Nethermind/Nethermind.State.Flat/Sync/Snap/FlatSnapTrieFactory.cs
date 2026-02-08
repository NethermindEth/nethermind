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
    private ILogger _logger = logManager.GetClassLogger<FlatSnapTrieFactory>();
    private bool _databaseInitiallyCleared = false;
    private Lock _lock = new Lock();

    public ISnapTree CreateStateTree()
    {
        EnsureDatabaseCleared();

        var reader = persistence.CreateReader();
        var writeBatch = persistence.CreateWriteBatch(reader.CurrentState, reader.CurrentState, WriteFlags.DisableWAL);
        return new FlatSnapStateTree(reader, writeBatch, logManager);
    }

    public ISnapTree CreateStorageTree(in ValueHash256 accountPath)
    {
        EnsureDatabaseCleared();

        var reader = persistence.CreateReader();
        var writeBatch = persistence.CreateWriteBatch(reader.CurrentState, reader.CurrentState, WriteFlags.DisableWAL);
        return new FlatSnapStorageTree(reader, writeBatch, accountPath.ToCommitment(), logManager);
    }

    private void EnsureDatabaseCleared()
    {
        if (_databaseInitiallyCleared) return;

        using (_lock.EnterScope())
        {
            if (_databaseInitiallyCleared) return;

            _logger.Info("Clearing database");
            persistence.Clear();

            _databaseInitiallyCleared = true;
        }
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
