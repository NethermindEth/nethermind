// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;
using static Nethermind.Benchmarks.State.FlatWorldStateBenchmarkHarness;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Storage-trie root recomputation. Unlike <see cref="WriteBatchBenchmark"/>, the measured
/// region calls <see cref="FlatWorldStateScope.Commit"/>, so the storage (and state) root is
/// actually hashed — that is the point of this benchmark, not an omission.
/// </summary>
[MemoryDiagnoser]
[NoTieredCompilation]
[MinIterationCount(40)]
public class StorageRootCommitBenchmark
{
    // Below this count, StorageTreeBulkWriteBatch writes each slot straight
    // into the storage trie (no buffering); above it, entries are buffered and flushed as one
    // BulkSet call. IndividualSetEstimate below is chosen to always stay under it regardless of
    // ChangedSlots, and BulkSetEstimate to always clear it, so the two benchmark methods exercise
    // the two code paths deliberately rather than incidentally by ChangedSlots' own magnitude.
    private const int BulkThreshold = StorageTreeBulkWriteBatch.MIN_ENTRIES_TO_BATCH;
    private const int IndividualSetEstimate = 0;

    private const int SlotCount = 10_000;
    private const ulong BaseBlockNumber = 1;

    private FlatDbConfig _config = null!;
    private ResourcePool _resourcePool = null!;
    private List<FlatSnapshot> _baseSnapshots = null!;
    private StateId _baseStateId;
    private Address _address = null!;

    private FlatWorldStateScope _scope = null!;

    [Params(1, 8, 100, 1000)]
    public int ChangedSlots { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        FlatWorldStateBenchmarkHarness.RequireTieredCompilationDisabled();

        _config = new FlatDbConfig();
        _resourcePool = new ResourcePool(_config);
        _baseSnapshots = new List<FlatSnapshot>(1);
        _address = DeriveAddress(1);

        StateId initialStateId = new(0, Keccak.EmptyTreeHash);

        SnapshotPooledList prevSnapshots = new(0);
        ReadOnlySnapshotBundle readOnly = new(
            prevSnapshots, new NoopPersistenceReader(), recordDetailedMetrics: false,
            PersistedSnapshotStack.Empty());
        NullTrieNodeCache cache = new();
        SnapshotBundle bundle = new(
            readOnly, cache, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        CapturingCommitTarget commitTarget = new();
        FlatWorldStateScope scope = new(
            currentStateId: initialStateId,
            snapshotBundle: bundle,
            codeDb: new NullCodeDb(),
            commitTarget: commitTarget,
            configuration: _config,
            trieCacheWarmer: new NoopTrieWarmer(),
            logManager: NullLogManager.Instance);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(1))
        {
            batch.Set(_address, new Account(balance: 1));

            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch =
                batch.CreateStorageWriteBatch(_address, estimatedEntries: SlotCount);
            for (int s = 0; s < SlotCount; s++)
            {
                storageBatch.Set((UInt256)(ulong)(s + 1), (UInt256)(byte)((s + 1) & 0xFF));
            }
        }

        scope.Commit(blockNumber: BaseBlockNumber);

        FlatSnapshot snapshot = commitTarget.LastSnapshot
            ?? throw new InvalidOperationException("GlobalSetup: commit produced no snapshot");
        snapshot.TryAcquire();
        _baseSnapshots.Add(snapshot);
        _baseStateId = new StateId(BaseBlockNumber, scope.RootHash);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        SnapshotPooledList prevSnapshots = new(_baseSnapshots.Count);
        foreach (FlatSnapshot s in _baseSnapshots)
        {
            s.TryAcquire();
            prevSnapshots.Add(s);
        }

        ReadOnlySnapshotBundle readOnly = new(
            prevSnapshots, new NoopPersistenceReader(), recordDetailedMetrics: false,
            PersistedSnapshotStack.Empty());
        NullTrieNodeCache cache = new();
        SnapshotBundle bundle = new(
            readOnly, cache, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        CapturingCommitTarget commitTarget = new();
        _scope = new FlatWorldStateScope(
            currentStateId: _baseStateId,
            snapshotBundle: bundle,
            codeDb: new NullCodeDb(),
            commitTarget: commitTarget,
            configuration: _config,
            trieCacheWarmer: new NoopTrieWarmer(),
            logManager: NullLogManager.Instance);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _scope?.Dispose();
        _scope = null!;
    }

    [Benchmark]
    public Hash256 CommitIndividualSets()
    {
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = _scope.StartWriteBatch(1))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch =
                batch.CreateStorageWriteBatch(_address, estimatedEntries: IndividualSetEstimate);
            for (int s = 0; s < ChangedSlots; s++)
            {
                storageBatch.Set((UInt256)(ulong)(s + 1), MutatedSlotValue(s));
            }
        }

        _scope.Commit(blockNumber: BaseBlockNumber + 1);
        return _scope.RootHash;
    }

    [Benchmark]
    public Hash256 CommitBulkWriteBatch()
    {
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = _scope.StartWriteBatch(1))
        {
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch =
                batch.CreateStorageWriteBatch(_address, estimatedEntries: Math.Max(ChangedSlots, BulkThreshold + 1));
            for (int s = 0; s < ChangedSlots; s++)
            {
                storageBatch.Set((UInt256)(ulong)(s + 1), MutatedSlotValue(s));
            }
        }

        _scope.Commit(blockNumber: BaseBlockNumber + 1);
        return _scope.RootHash;
    }

    // Guaranteed different from the (byte)((s + 1) & 0xFF) fill value GlobalSetup wrote (max 255),
    // so every changed slot's value actually changes and the storage root is forced to move.
    private static UInt256 MutatedSlotValue(int slotIndex) => (UInt256)(ulong)(1_000_000 + slotIndex + 1);
}
