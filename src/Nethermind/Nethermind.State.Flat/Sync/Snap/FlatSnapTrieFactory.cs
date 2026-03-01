// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTrieFactory implementation for flat state storage.
/// Uses IPersistence to create reader/writeBatch per tree for proper resource management.
/// </summary>
public class FlatSnapTrieFactory(IPersistence persistence, ILogManager logManager) : ISnapTrieFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatSnapTrieFactory>();
    private readonly Lock _lock = new Lock();

    private bool _initialized = false;

    public ISnapTree CreateStateTree()
    {
        EnsureDatabaseCleared();

        IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        return new FlatSnapStateTree(reader, writeBatch, logManager);
    }

    public ISnapTree CreateStorageTree(in ValueHash256 accountPath)
    {
        EnsureDatabaseCleared();

        IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        return new FlatSnapStorageTree(reader, writeBatch, accountPath.ToCommitment(), logManager);
    }

    private void EnsureDatabaseCleared()
    {
        if (_initialized) return;

        using (_lock.EnterScope())
        {
            if (_initialized) return;
            _initialized = true;

            _logger.Info("Clearing database");
            persistence.Clear();
        }
    }
}
