// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie.NodeBuffer;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;
using static Nethermind.Benchmarks.State.FlatWorldStateBenchmarkHarness;

namespace Nethermind.Benchmarks.State;

/// <summary>Writes a persisted snapshot into the flat database through <see cref="PersistenceManager.PersistPersistedSnapshot"/>.</summary>
[MemoryDiagnoser]
public class PersistedSnapshotPersistBenchmark
{
    private const int Entries = 10_000;

    public enum SnapshotContent
    {
        /// <summary><see cref="Entries"/> accounts, half of them contracts.</summary>
        Accounts,
        /// <summary>One contract with <see cref="Entries"/> slots, so nearly all entries are storage trie nodes.</summary>
        StorageNodes,
    }

    [Params(SnapshotContent.Accounts, SnapshotContent.StorageNodes)]
    public SnapshotContent Content { get; set; }

    private string _directory = null!;
    private ArenaManager _arena = null!;
    private BlobArenaManager _blobs = null!;
    private SnapshotRepository _repository = null!;
    private PersistenceManager _persistenceManager = null!;
    private PersistedSnapshot _persisted = null!;

    [GlobalSetup]
    public void Setup()
    {
        FlatDbConfig config = new() { ApplyStorageWritesOnIdleThread = false };
        ResourcePool resourcePool = new(config);
        FlatSnapshot snapshot = BuildSnapshot(config, resourcePool);

        _directory = Path.Combine(Path.GetTempPath(), $"{nameof(PersistedSnapshotPersistBenchmark)}-{Guid.NewGuid():N}");
        _arena = new ArenaManager(Path.Combine(_directory, "arena"), config, NullLogManager.Instance);
        _blobs = new BlobArenaManager(Path.Combine(_directory, "blob"), config.ArenaFileSizeBytes);
        SnapshotCatalog catalog = new(new MemDb());
        _repository = new SnapshotRepository(_arena, _blobs, catalog, config, UnavailableStateHeaderProvider.Instance, NullLogManager.Instance);
        PersistedSnapshotLoader loader = new(_repository, _arena, _blobs, catalog, config, NullLogManager.Instance);
        loader.Load();
        loader.ConvertAndRegister(snapshot);

        using (PersistedSnapshotList bases = _repository.LeaseBaseSnapshotsInRange(snapshot.From, snapshot.To))
        {
            _persisted = bases[0];
            _persisted.TryAcquire();
        }

        snapshot.Dispose();

        RewritingPersistence persistence = new(new RocksDbPersistence(new SnapshotableMemColumnsDb<FlatDbColumns>(), NullLogManager.Instance, NullTrieNodeLog.Instance));
        _persistenceManager = new PersistenceManager(
            config,
            new CompactionSchedule(new MemDb(), config, NullLogManager.Instance),
            UnavailableStateHeaderProvider.Instance,
            persistence,
            _repository,
            NullStatePersistenceBarrier.Instance,
            NullLogManager.Instance,
            NullPersistedSnapshotCompactor.Instance,
            loader,
            new ProcessExitSource(CancellationToken.None));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _persistenceManager.Dispose();
        _persisted.Dispose();
        _repository.Dispose();
        _blobs.Dispose();
        _arena.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark]
    public void Persist() => _persistenceManager.PersistPersistedSnapshot(_persisted);

    private FlatSnapshot BuildSnapshot(FlatDbConfig config, ResourcePool resourcePool)
    {
        using INodeRecordBuffer buffer = new NodeRecordBuffer(0, NodeRecordBuffer.DefaultGenerationBytes);
        SnapshotBundle bundle = new(
            new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new NoopPersistenceReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty(), buffer.PinAll()),
            buffer, resourcePool, ResourcePool.Usage.MainBlockProcessing);
        CapturingCommitTarget commitTarget = new();
        using FlatWorldStateScope scope = new(StateId.PreGenesis, bundle, new NullCodeDb(), commitTarget, config, new NoopTrieWarmer(), NullLogManager.Instance);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(Entries))
        {
            if (Content == SnapshotContent.Accounts)
            {
                for (int i = 0; i < Entries; i++)
                {
                    batch.Set(DeriveAddress(i), i % 2 == 0
                        ? new Account((ulong)i, (UInt256)(ulong)(i + 1))
                        : new Account((ulong)i, (UInt256)(ulong)(i + 1), Keccak.Compute(BitConverter.GetBytes(i)), Keccak.Compute(BitConverter.GetBytes(-i))));
                }
            }
            else
            {
                Address storageAddress = DeriveAddress(0);
                batch.Set(storageAddress, new Account(1));
                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(storageAddress, estimatedEntries: Entries);
                for (int s = 0; s < Entries; s++) storageBatch.Set((UInt256)(ulong)s, (UInt256)(ulong)(s + 1));
            }
        }

        scope.Commit(blockNumber: 0);
        return commitTarget.LastSnapshot ?? throw new InvalidOperationException("The commit produced no snapshot");
    }

    /// <summary>Lets the same snapshot be written again on top of itself, so every invocation writes the same data.</summary>
    private sealed class RewritingPersistence(IPersistence inner) : IPersistence
    {
        private StateId? _current;

        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => inner.CreateReader(flags);

        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None)
        {
            IPersistence.IWriteBatch batch = inner.CreateWriteBatch(_current ?? from, to, flags);
            _current = to;
            return batch;
        }

        public void Flush() => inner.Flush();
        public void Clear() => inner.Clear();
    }
}
