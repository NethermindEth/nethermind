// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
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
/// Tracks in-flight trees so callers can wait for all writeBatches to drain before relying
/// on the persisted state being complete.
/// </summary>
public class FlatSnapTrieFactory(IPersistence persistence, ISyncConfig syncConfig, ILogManager logManager) : ISnapTrieFactory
{
    private readonly ILogger _logger = logManager.GetClassLogger<FlatSnapTrieFactory>();
    private readonly Lock _lock = new();

    private volatile bool _initialized;

    // Tracks ISnapTree instances created via this factory that haven't been disposed yet.
    // The dispose path commits the per-tree IWriteBatch; until that completes, the tree's
    // accumulated writes aren't yet visible to subsequent IPersistence readers. Callers
    // that need a consistent post-sync view (e.g. FlatTreeSyncStore.FinalizeSync) drain via
    // <see cref="WaitForInFlightTreesDrained"/>.
    private long _inFlightTrees;

    internal void OnTreeCreated() => Interlocked.Increment(ref _inFlightTrees);

    internal void OnTreeDisposed() => Interlocked.Decrement(ref _inFlightTrees);

    /// <summary>
    /// Spin-wait until all trees handed out by this factory have been disposed. Returns true
    /// if drained within <paramref name="timeout"/>, false on timeout.
    /// </summary>
    public bool WaitForInFlightTreesDrained(TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        SpinWait spin = new();
        while (Interlocked.Read(ref _inFlightTrees) > 0)
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                if (_logger.IsWarn) _logger.Warn($"FlatSnapTrieFactory: {Interlocked.Read(ref _inFlightTrees)} trees still in flight after {timeout}");
                return false;
            }
            spin.SpinOnce();
        }
        return true;
    }

    public ISnapTree<PathWithAccount> CreateStateTree()
    {
        EnsureDatabaseCleared();

        IPersistence.IPersistenceReader? reader = null;
        IPersistence.IWriteBatch? writeBatch = null;
        bool treeTracked = false;
        try
        {
            reader = persistence.CreateReader(ReaderFlags.Sync);
            writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
            OnTreeCreated();
            treeTracked = true;
            return new FlatSnapStateTree(reader, writeBatch, syncConfig.EnableSnapDoubleWriteCheck, logManager, this);
        }
        catch
        {
            if (treeTracked) OnTreeDisposed();
            writeBatch?.Dispose();
            reader?.Dispose();
            throw;
        }
    }

    public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath)
    {
        EnsureDatabaseCleared();

        IPersistence.IPersistenceReader? reader = null;
        IPersistence.IWriteBatch? writeBatch = null;
        bool treeTracked = false;
        try
        {
            reader = persistence.CreateReader(ReaderFlags.Sync);
            writeBatch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
            OnTreeCreated();
            treeTracked = true;
            return new FlatSnapStorageTree(reader, writeBatch, accountPath.ToCommitment(), syncConfig.EnableSnapDoubleWriteCheck, logManager, this);
        }
        catch
        {
            if (treeTracked) OnTreeDisposed();
            writeBatch?.Dispose();
            reader?.Dispose();
            throw;
        }
    }

    private void EnsureDatabaseCleared()
    {
        if (_initialized) return;

        using (_lock.EnterScope())
        {
            if (_initialized) return;

            _logger.Info("Clearing database");
            persistence.Clear();

            // Set _initialized AFTER Clear completes so a concurrent caller can't see _initialized=true
            // and proceed to write accounts while Clear is still iterating GetAllKeys / queueing
            // Removes in its write batch — that race deletes freshly-written accounts when Clear's
            // batch finally disposes. Volatile (via the field declaration) gives the publish ordering.
            _initialized = true;
        }
    }
}
