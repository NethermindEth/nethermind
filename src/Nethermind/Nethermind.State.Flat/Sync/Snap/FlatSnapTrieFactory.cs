// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.State.Flat.Sync.Snap;

/// <summary>
/// ISnapTrieFactory implementation for flat state storage.
/// Uses IPersistence to create reader/writeBatch per tree for proper resource management.
/// EnsureInitialize/FinalizeSync are driven by the snap-sync runner at start/end of the run,
/// so they don't need internal locking — they're never called concurrently with CreateXxxTree.
/// </summary>
public class FlatSnapTrieFactory(IPersistence persistence, ISyncConfig syncConfig, ILogManager logManager) : ISnapTrieFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatSnapTrieFactory>();

    public void EnsureInitialize()
    {
        if (_logger.IsInfo) _logger.Info("Clearing database");
        persistence.Clear();
    }

    public void FinalizeSync() => persistence.Flush();

    public ISnapTree<PathWithAccount> CreateStateTree()
    {
        IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        return new FlatSnapStateTree(reader, writeBatch, syncConfig.EnableSnapDoubleWriteCheck, logManager);
    }

    public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath)
    {
        IPersistence.IPersistenceReader reader = persistence.CreateReader(ReaderFlags.Sync);
        IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        return new FlatSnapStorageTree(reader, writeBatch, accountPath.ToCommitment(), syncConfig.EnableSnapDoubleWriteCheck, logManager);
    }
}
