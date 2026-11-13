// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Migration;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Account and slot reads on a node migrating from the pruning trie state, for what the migration has not copied yet: each
/// read walks the trie from the state root, reading every node from flat and then the HalfPath DB.
/// </summary>
/// <remarks>The bundle holds no snapshot and flat only its state pointer, so every node takes the fallback; both stores are in memory.</remarks>
[MemoryDiagnoser]
public class HalfPathFallbackReadBenchmark
{
    private const int Slots = 1_000;

    [Params(100_000)]
    public int Accounts { get; set; }

    private MemDb _halfPathDb = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _flatDb = null!;
    private ReadOnlySnapshotBundle _bundle = null!;
    private Address[] _addresses = null!;
    private Address _storageAccount = null!;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _halfPathDb = new MemDb();
        NodeStorage halfPath = new(_halfPathDb);
        StateTree stateTree = new(new RawScopedTrieStore(halfPath), LimboLogs.Instance);
        _addresses = new Address[Accounts];
        for (int i = 0; i < Accounts; i++)
        {
            _addresses[i] = AddressOf(i);
            stateTree.Set(_addresses[i], new Account((UInt256)i + 1));
        }

        _storageAccount = AddressOf(-1);
        StorageTree storageTree = new(new RawScopedTrieStore(halfPath, _storageAccount.ToAccountPath.ToCommitment()), LimboLogs.Instance);
        for (int slot = 0; slot < Slots; slot++) storageTree.Set((UInt256)slot, [(byte)(slot % 255 + 1)]);
        storageTree.Commit();
        stateTree.Set(_storageAccount, new Account(1).WithChangedStorageRoot(storageTree.RootHash));
        stateTree.Commit();

        _flatDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        IPersistence flat = new RocksDbPersistence(_flatDb, LimboLogs.Instance, NullTrieNodeLog.Instance);
        flat.CreateWriteBatch(StateId.PreGenesis, new StateId(1, stateTree.RootHash)).Dispose();
        BasePersistence.WriteMigrationCursor(_flatDb.GetColumnDb(FlatDbColumns.Metadata), MigrationCursor.Start);
        HalfPathMigrationCursor cursor = new(_flatDb);
        _bundle = new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new HalfPathFallbackPersistence(flat, halfPath, cursor).CreateReader(),
            recordDetailedMetrics: false, PersistedSnapshotStack.Empty(), bufferPin: null);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bundle.Dispose();
        _flatDb.Dispose();
        _halfPathDb.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Account? ExistingAccount() => _bundle.GetAccount(_addresses[_next++ % _addresses.Length]);

    [Benchmark]
    public Account? MissingAccount() => _bundle.GetAccount(AddressOf(Accounts + _next++));

    [Benchmark]
    public bool ExistingSlot()
    {
        _bundle.GetSlot(_storageAccount, (UInt256)(_next++ % Slots), selfDestructStateIdx: -1, out UInt256? value);
        return value is not null;
    }

    private static Address AddressOf(int index) => new(Keccak.Compute(BitConverter.GetBytes(index)).Bytes[..Address.Size]);
}
