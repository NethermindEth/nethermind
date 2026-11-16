// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
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
using Nethermind.State.Flat.PersistedSnapshots.Storage;
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

    private string _directory = null!;
    private ArenaManager _arena = null!;
    private BlobArenaManager _blobs = null!;
    private SnapshotRepository _repository = null!;
    private StateId _head;
    private readonly SnapshotableMemColumnsDb<FlatDbColumns> _db = new();
    private HistoryWriter _writer = null!;
    private FlatDbConfig _config = null!;

    [GlobalSetup]
    public void Setup()
    {
        _config = new FlatDbConfig
        {
            ApplyStorageWritesOnIdleThread = false,
            HistoryEnabled = true,
            Layout = FlatLayout.Flat,
            HistoryRetention = Windowed ? HistoryRetentionMode.Rolling : HistoryRetentionMode.None,
            HistoryRetentionBlocks = Windowed ? 100UL : 0UL
        };
        FlatSnapshot snapshot = BuildSnapshot(_config, new ResourcePool(_config));
        _head = snapshot.To;

        _directory = Path.Combine(Path.GetTempPath(), $"{nameof(FlatHistoryAccountBenchmarks)}-{Guid.NewGuid():N}");
        _arena = new ArenaManager(Path.Combine(_directory, "arena"), _config, NullLogManager.Instance);
        _blobs = new BlobArenaManager(Path.Combine(_directory, "blob"), _config.ArenaFileSizeBytes);
        SnapshotCatalog catalog = new(new MemDb());
        _repository = new SnapshotRepository(_arena, _blobs, catalog, _config, UnavailableStateHeaderProvider.Instance, NullLogManager.Instance);
        PersistedSnapshotLoader loader = new(_repository, _arena, _blobs, catalog, _config, NullLogManager.Instance);
        loader.Load();
        loader.ConvertAndRegister(snapshot);
        snapshot.Dispose();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _repository.Dispose();
        _blobs.Dispose();
        _arena.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [IterationSetup]
    public void CreateHistory()
    {
        SnapshotableMemColumnsDb<FlatHistoryColumns> history = new();
        HistoryAvailability availability = new(history.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        _writer = new HistoryWriter(_db, history, _config, availability, HistoryRowFormat.Resolve(availability, _config), NullLogManager.Instance, commitments: null);
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
