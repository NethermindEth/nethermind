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
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Account and slot reads on a node migrating from the pruning trie state, for what the migration has not copied yet: each
/// flat miss walks the trie from the state root, reading every node from flat and then the HalfPath DB.
/// </summary>
/// <remarks>Flat holds only its state pointer, so every read takes the fallback; both stores are in memory.</remarks>
[MemoryDiagnoser]
public class HalfPathFallbackReadBenchmark
{
    private const int Slots = 1_000;

    [Params(100_000)]
    public int Accounts { get; set; }

    private MemDb _halfPathDb = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _flatDb = null!;
    private IPersistence.IPersistenceReader _reader = null!;
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
        HalfPathMigrationCursor cursor = new(_flatDb);
        cursor.Advance(MigrationCursor.Start);
        _reader = new HalfPathFallbackPersistence(flat, halfPath, cursor).CreateReader();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reader.Dispose();
        _flatDb.Dispose();
        _halfPathDb.Dispose();
    }

    [Benchmark(Baseline = true)]
    public Account? ExistingAccount() => _reader.GetAccount(_addresses[_next++ % _addresses.Length]);

    [Benchmark]
    public Account? MissingAccount() => _reader.GetAccount(AddressOf(Accounts + _next++));

    [Benchmark]
    public bool ExistingSlot()
    {
        UInt256 value = 0;
        return _reader.TryGetSlot(_storageAccount, (UInt256)(_next++ % Slots), ref value);
    }

    private static Address AddressOf(int index) => new(Keccak.Compute(BitConverter.GetBytes(index)).Bytes[..Address.Size]);
}
