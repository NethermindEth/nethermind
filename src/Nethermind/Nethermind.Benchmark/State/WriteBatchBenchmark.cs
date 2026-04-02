// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;

namespace Nethermind.Benchmarks.State;

[MemoryDiagnoser]
[WarmupCount(3)]
[MinIterationCount(3)]
[MaxIterationCount(10)]
public class WriteBatchBenchmark
{
    private const int SnapshotCount = 1;

    private FlatDbConfig _config = null!;
    private ResourcePool _resourcePool = null!;
    private List<FlatSnapshot> _baseSnapshots = null!;
    private StateId _currentStateId;
    private Address[] _addresses = null!;

    private FlatWorldStateScope _scope = null!;

    [Params(100, 500)]
    public int AccountCount { get; set; }

    [Params(100, 1000, 3000)]
    public int StorageSlotsPerAccount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _config = new FlatDbConfig();
        _resourcePool = new ResourcePool(_config);
        _baseSnapshots = new List<FlatSnapshot>(SnapshotCount);
        _currentStateId = new StateId(0, Keccak.EmptyTreeHash);

        int totalAccountCount = 0;

        for (int block = 0; block < SnapshotCount; block++)
        {
            int accountCount = 500;
            int storageAccountCount = 10;
            int slotsPerStorageAccount = 50;

            SnapshotPooledList prevSnapshots = new SnapshotPooledList(_baseSnapshots.Count);
            foreach (FlatSnapshot s in _baseSnapshots)
            {
                s.TryAcquire();
                prevSnapshots.Add(s);
            }

            ReadOnlySnapshotBundle readOnly = new ReadOnlySnapshotBundle(
                prevSnapshots, new NoopPersistenceReader(), recordDetailedMetrics: false);
            NullTrieNodeCache cache = new NullTrieNodeCache();
            SnapshotBundle bundle = new SnapshotBundle(
                readOnly, cache, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
            CapturingCommitTarget commitTarget = new CapturingCommitTarget();
            FlatWorldStateScope scope = new FlatWorldStateScope(
                currentStateId: _currentStateId,
                snapshotBundle: bundle,
                codeDb: new NullCodeDb(),
                commitTarget: commitTarget,
                configuration: _config,
                trieCacheWarmer: new NoopTrieWarmer(),
                logManager: NullLogManager.Instance);

            int addressOffset = totalAccountCount;
            Address[] addresses = new Address[accountCount];
            Parallel.For(0, accountCount, i =>
            {
                addresses[i] = DeriveAddress(addressOffset + i + 1);
            });

            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch =
                scope.StartWriteBatch(accountCount))
            {
                IWorldStateScopeProvider.IStorageWriteBatch[] storageBatches =
                    new IWorldStateScopeProvider.IStorageWriteBatch[storageAccountCount];
                for (int i = 0; i < accountCount; i++)
                {
                    batch.Set(addresses[i], new Account(balance: (UInt256)(addressOffset + i + 1)));

                    if (i < storageAccountCount)
                    {
                        storageBatches[i] = batch.CreateStorageWriteBatch(addresses[i],
                            estimatedEntries: slotsPerStorageAccount);
                    }
                }

                int slots = slotsPerStorageAccount;
                Parallel.For(0, storageAccountCount, i =>
                {
                    IWorldStateScopeProvider.IStorageWriteBatch storageBatch = storageBatches[i];
                    for (int s = 0; s < slots; s++)
                    {
                        storageBatch.Set((UInt256)(ulong)(s + 1),
                            new byte[] { (byte)((s + 1) & 0xFF) });
                    }

                    storageBatch.Dispose();
                });
            }

            scope.Commit(blockNumber: block + 1);

            FlatSnapshot snapshot = commitTarget.LastSnapshot
                ?? throw new InvalidOperationException(
                    $"Block {block + 1}: Commit produced no snapshot");
            snapshot.TryAcquire();
            _baseSnapshots.Add(snapshot);

            _currentStateId = new StateId(block + 1, scope.RootHash);
            totalAccountCount += accountCount;
        }

        // Pre-compute addresses for benchmark iterations
        _addresses = new Address[AccountCount];
        Parallel.For(0, AccountCount, i =>
        {
            _addresses[i] = DeriveAddress(totalAccountCount + i + 1);
        });
    }

    [IterationSetup]
    public void IterationSetup()
    {
        SnapshotPooledList prevSnapshots = new SnapshotPooledList(_baseSnapshots.Count);
        foreach (FlatSnapshot s in _baseSnapshots)
        {
            s.TryAcquire();
            prevSnapshots.Add(s);
        }

        ReadOnlySnapshotBundle readOnly = new ReadOnlySnapshotBundle(
            prevSnapshots, new NoopPersistenceReader(), recordDetailedMetrics: false);
        NullTrieNodeCache cache = new NullTrieNodeCache();
        SnapshotBundle bundle = new SnapshotBundle(
            readOnly, cache, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        CapturingCommitTarget commitTarget = new CapturingCommitTarget();
        _scope = new FlatWorldStateScope(
            currentStateId: _currentStateId,
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
    public void BatchWriteAccount()
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch =
            _scope.StartWriteBatch(AccountCount);
        for (int i = 0; i < AccountCount; i++)
        {
            batch.Set(_addresses[i], new Account(balance: (UInt256)(ulong)(i + 1)));
        }
    }

    [Benchmark]
    public void BatchWriteStorage()
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch =
            _scope.StartWriteBatch(AccountCount);

        for (int i = 0; i < AccountCount; i++)
        {
            batch.Set(_addresses[i], new Account(balance: (UInt256)(ulong)(i + 1)));

            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch =
                batch.CreateStorageWriteBatch(_addresses[i], estimatedEntries: StorageSlotsPerAccount);
            for (int s = 0; s < StorageSlotsPerAccount; s++)
            {
                storageBatch.Set((UInt256)(ulong)(s + 1),
                    new byte[] { (byte)((s + 1) & 0xFF) });
            }
        }
    }

    [Benchmark]
    public void ParallelBatchWriteStorage()
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch =
            _scope.StartWriteBatch(AccountCount);

        // Phase 1 (sequential): set accounts and create storage batches
        IWorldStateScopeProvider.IStorageWriteBatch[] storageBatches =
            new IWorldStateScopeProvider.IStorageWriteBatch[AccountCount];
        for (int i = 0; i < AccountCount; i++)
        {
            batch.Set(_addresses[i], new Account(balance: (UInt256)(ulong)(i + 1)));
            storageBatches[i] = batch.CreateStorageWriteBatch(_addresses[i],
                estimatedEntries: StorageSlotsPerAccount);
        }

        // Phase 2 (parallel): fill storage slots
        int slots = StorageSlotsPerAccount;
        Parallel.For(0, AccountCount, i =>
        {
            IWorldStateScopeProvider.IStorageWriteBatch storageBatch = storageBatches[i];
            for (int s = 0; s < slots; s++)
            {
                storageBatch.Set((UInt256)(ulong)(s + 1),
                    new byte[] { (byte)((s + 1) & 0xFF) });
            }

            storageBatch.Dispose();
        });
    }

    private static Address DeriveAddress(int index) =>
        new Address(Keccak.Compute(Address.FromNumber((UInt256)(ulong)index).Bytes));

    private sealed class NullTrieNodeCache : ITrieNodeCache
    {
        public bool TryGet(Hash256 address, in TreePath path, Hash256 hash, out TrieNode node)
        {
            node = null;
            return false;
        }

        public void Add(TransientResource transientResource) { }

        public void Clear() { }
    }

    private sealed class CapturingCommitTarget : IFlatCommitTarget
    {
        public FlatSnapshot LastSnapshot { get; private set; }
        public TransientResource LastResource { get; private set; }

        public void AddSnapshot(FlatSnapshot snapshot, TransientResource transientResource)
        {
            LastSnapshot = snapshot;
            LastResource = transientResource;
        }
    }

    private sealed class NullCodeDb : IWorldStateScopeProvider.ICodeDb
    {
        public byte[] GetCode(in ValueHash256 codeHash) => null;

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite()
            => NullCodeSetter.Instance;

        private sealed class NullCodeSetter : IWorldStateScopeProvider.ICodeSetter
        {
            public static readonly NullCodeSetter Instance = new NullCodeSetter();

            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) { }

            public void Dispose() { }
        }
    }
}
