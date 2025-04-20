// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.State;

// Right now to memory tight to be useful.
[MemoryDiagnoser]
public class StorageAccessBenchmark
{
    private WorldState _preCached;
    private WorldState _notCached;

    private const uint MaxPrecalculatedIndex = 1024;

    private static readonly Address Account = new(Keccak.Compute("test"));
    private static readonly StorageCell StorageCell = new(Account, MaxPrecalculatedIndex / 2);

    [GlobalSetup]
    public void Setup()
    {
        var preCache = new PreBlockCaches();
        var code = new MemDb();

        _preCached = new WorldState(new TrieStore(new MemDb("storage"), NullLogManager.Instance), code,
            LimboLogs.Instance, preCache, false);

        _notCached = new WorldState(new TrieStore(new MemDb("storage"), NullLogManager.Instance), code,
            LimboLogs.Instance, null, false);

        _notCached.CreateAccount(Account, 100, 100);

        for (uint i = 0; i < MaxPrecalculatedIndex; i++)
        {
            var cell = new StorageCell(Account, i);
            var value = new StorageValue(i);

            preCache.StorageCache[cell] = value;
            _preCached.Set(cell, value);
            _notCached.Set(cell, value);
        }

        _preCached.Commit(Prague.Instance);
        _preCached.CommitTree(123);
        _notCached.Commit(Prague.Instance);
        _notCached.CommitTree(123);
    }

    //[Benchmark]
    public bool PreCached_small_indexes()
    {
        _preCached.Reset(true);

        return _preCached
            .Get(StorageCell)
            .IsZero;
    }

    //[Benchmark]
    public bool NotCached_small_indexes()
    {
        _notCached.Reset(true);

        return _notCached
            .Get(StorageCell)
            .IsZero;
    }
}
