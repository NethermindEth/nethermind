// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using Nethermind.State.Flat.History.Changesets;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie.NodeBuffer;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;
using static Nethermind.Benchmarks.State.FlatWorldStateBenchmarkHarness;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Captures a persisted snapshot of <see cref="Accounts"/> changed accounts, half of them contracts, into flat history.
/// </summary>
[MemoryDiagnoser]
public class FlatHistoryAccountBenchmarks
{
    private const int Accounts = 10_000;

    [Params(false, true)]
    public bool Windowed { get; set; }

    /// <summary>Drops history rows instead of copying them into the in-memory database, so only the capture code allocates.</summary>
    [Params(false, true)]
    public bool DiscardRows { get; set; }

    private TempPath _dbPath = null!;
    private IContainer _container = null!;
    private ILifetimeScope _historyScope;
    private ISnapshotRepository _repository = null!;
    private StateId _head;
    private HistoryWriter _writer = null!;

    [GlobalSetup]
    public void Setup()
    {
        FlatDbConfig config = new()
        {
            Enabled = true,
            ApplyStorageWritesOnIdleThread = false,
            HistoryEnabled = true,
            Layout = FlatLayout.Flat,
            HistoryRetention = Windowed ? HistoryRetentionMode.Rolling : HistoryRetentionMode.None,
            HistoryRetentionBlocks = Windowed ? 100UL : 0UL
        };
        _dbPath = TempPath.GetTempDirectory();
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(config, new InitConfig { BaseDbPath = _dbPath.Path }))
            .AddSingleton<IPersistedSnapshotCompactor>(NullPersistedSnapshotCompactor.Instance)
            .Build();

        FlatSnapshot snapshot = BuildSnapshot(config, _container.Resolve<ResourcePool>());
        _head = snapshot.To;
        _repository = _container.Resolve<ISnapshotRepository>();
        // The test module turns off long finality, so the interface resolves to the no-op loader.
        PersistedSnapshotLoader loader = _container.Resolve<PersistedSnapshotLoader>();
        loader.Load();
        loader.ConvertAndRegister(snapshot);
        snapshot.Dispose();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _historyScope?.Dispose();
        _container.Dispose();
        _dbPath.Dispose();
    }

    [IterationSetup]
    public void CreateHistory()
    {
        _historyScope?.Dispose();
        IColumnsDb<FlatHistoryColumns> history = DiscardRows ? new RowDiscardingHistoryDb() : new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        // Each invocation captures into an empty history, so the history module is loaded again over a fresh database.
        _historyScope = _container.BeginLifetimeScope(builder => builder
            .AddModule(new FlatHistoryModule())
            .AddSingleton(history));
        _writer = _historyScope.Resolve<HistoryWriter>();
    }

    [Benchmark]
    public void Capture() => _writer.CaptureUpTo(_head, _repository, CancellationToken.None);

    private FlatSnapshot BuildSnapshot(FlatDbConfig config, ResourcePool resourcePool)
    {
        using INodeRecordBuffer buffer = new NodeRecordBuffer(0, NodeRecordBuffer.DefaultGenerationBytes);
        SnapshotBundle bundle = new(
            new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new NoopPersistenceReader(), recordDetailedMetrics: false, PersistedSnapshotStack.Empty(), buffer.PinAll()),
            buffer, resourcePool, ResourcePool.Usage.MainBlockProcessing);
        CapturingCommitTarget commitTarget = new();
        using FlatWorldStateScope scope = new(StateId.PreGenesis, bundle, new NullCodeDb(), commitTarget, config, new NoopTrieWarmer(), NullLogManager.Instance);

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(Accounts))
        {
            for (int i = 0; i < Accounts; i++)
            {
                batch.Set(DeriveAddress(i), i % 2 == 0
                    ? new Account((ulong)i, (UInt256)(ulong)(i + 1))
                    : new Account((ulong)i, (UInt256)(ulong)(i + 1), Keccak.Compute(BitConverter.GetBytes(i)), Keccak.Compute(BitConverter.GetBytes(-i))));
            }
        }

        scope.Commit(blockNumber: 0);
        return commitTarget.LastSnapshot ?? throw new InvalidOperationException("The commit produced no snapshot");
    }

    /// <summary>Keeps block markers, but drops account, storage and clear rows the way a native write batch takes them: without a managed copy.</summary>
    private sealed class RowDiscardingHistoryDb : IColumnsDb<FlatHistoryColumns>
    {
        private readonly SnapshotableMemColumnsDb<FlatHistoryColumns> _inner = new();

        public IDb GetColumnDb(FlatHistoryColumns key) => _inner.GetColumnDb(key);
        public IEnumerable<FlatHistoryColumns> ColumnKeys => _inner.ColumnKeys;
        public IColumnsWriteBatch<FlatHistoryColumns> StartWriteBatch() => new Batch(_inner.StartWriteBatch());
        public IColumnDbSnapshot<FlatHistoryColumns> CreateSnapshot() => _inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => _inner.Flush(onlyWal);
        public void Dispose() => _inner.Dispose();

        private sealed class Batch(IColumnsWriteBatch<FlatHistoryColumns> inner) : IColumnsWriteBatch<FlatHistoryColumns>
        {
            public IWriteBatch GetColumnBatch(FlatHistoryColumns key) =>
                key is FlatHistoryColumns.AccountHistory or FlatHistoryColumns.StorageHistory or FlatHistoryColumns.StorageClears
                    ? DiscardingWriteBatch.Instance
                    : inner.GetColumnBatch(key);

            public void Clear() => inner.Clear();
            public void Dispose() => inner.Dispose();
        }

        private sealed class DiscardingWriteBatch : IWriteBatch
        {
            public static readonly DiscardingWriteBatch Instance = new();

            public void Set(ReadOnlySpan<byte> key, byte[] value, WriteFlags flags = WriteFlags.None) { }
            public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) { }
            public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) { }
            public void Clear() { }
            public void Dispose() { }
        }
    }
}

/// <summary>Reads accounts from a bulk-fill scratch state, half of them contracts.</summary>
[MemoryDiagnoser]
public class BulkFillStateReaderBenchmarks
{
    private const int Accounts = 10_000;

    private BulkFillStateReader _reader = null!;
    private Address[] _addresses = null!;

    [GlobalSetup]
    public void Setup()
    {
        MemColumnsDb<BulkFillScratchState.Columns> db = new();
        IDb accounts = db.GetColumnDb(BulkFillScratchState.Columns.Accounts);
        _addresses = new Address[Accounts];
        for (int i = 0; i < Accounts; i++)
        {
            _addresses[i] = DeriveAddress(i);
            Account account = i % 2 == 0
                ? new Account((ulong)i, (UInt256)(ulong)(i + 1))
                : new Account((ulong)i, (UInt256)(ulong)(i + 1), Keccak.Compute(BitConverter.GetBytes(i)), Keccak.Compute(BitConverter.GetBytes(-i)));
            accounts[_addresses[i].ToAccountPath.Bytes] = AccountDecoder.Slim.Encode(account);
        }

        _reader = new BulkFillStateReader(db, new StateId(1, Keccak.EmptyTreeHash), rlpWrappedSlots: false);
    }

    [Benchmark(OperationsPerInvoke = Accounts)]
    public bool GetAccounts()
    {
        bool found = false;
        foreach (Address address in _addresses) found |= _reader.GetAccount(address) is not null;
        return found;
    }
}
