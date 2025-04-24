// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.State;

[DisassemblyDiagnoser]
[MemoryDiagnoser]
public class StorageAccessBenchmark
{
    private WorldState _preCached;
    private WorldState _notCached;
    private StorageValueMap _map;

    private const uint MaxPrecalculatedIndex = 1024;

    private static readonly Address Account = new(Keccak.Compute("test"));
    private static readonly StorageCell A = new(Account, MaxPrecalculatedIndex / 2);
    private static readonly StorageCell B = new(Account, MaxPrecalculatedIndex / 2 - 1);
    private static readonly StorageCell C = new(Account, MaxPrecalculatedIndex / 2 - 2);
    private static readonly StorageCell D = new(Account, MaxPrecalculatedIndex / 2 - 3);

    private static readonly StorageValue ValueA = new(new UInt256(1));
    private static readonly StorageValue ValueB = new(new UInt256(2));
    private static readonly StorageValue ValueC = new(new UInt256(3));
    private static readonly StorageValue ValueD = new(new UInt256(4));

    [GlobalSetup]
    public void Setup()
    {
        var preCache = new PreBlockCaches();
        var code = new MemDb();
        _map = new StorageValueMap();

        _preCached = new WorldState(new TrieStore(new MemDb("storage"), NullLogManager.Instance), code,
            LimboLogs.Instance, preCache, false);

        _notCached = new WorldState(new TrieStore(new MemDb("storage"), NullLogManager.Instance), code,
            LimboLogs.Instance, null, false);

        _notCached.CreateAccount(Account, 100, 100);

        for (uint i = 0; i < MaxPrecalculatedIndex; i++)
        {
            var cell = new StorageCell(Account, i);
            var value = new StorageValue(i);

            preCache.StorageCache[cell] = _map.Map(value);
            _preCached.Set(cell, value);
            _notCached.Set(cell, value);
        }

        _preCached.Commit(Prague.Instance);
        _preCached.CommitTree(123);
        _notCached.Commit(Prague.Instance);
        _notCached.CommitTree(123);
    }

    [Benchmark]
    public void Just_reset()
    {
        _notCached.Reset(true);
    }

    [Benchmark(OperationsPerInvoke = 4)]
    public bool PreCached_small_indexes()
    {
        _preCached.Reset(true);

        return
            _preCached.Get(A).IsZero &&
            _preCached.Get(B).IsZero &&
            _preCached.Get(C).IsZero &&
            _preCached.Get(D).IsZero;
    }

    [Benchmark(OperationsPerInvoke = 4)]
    public bool NotCached_small_indexes()
    {
        _notCached.Reset(true);

        return
            _notCached.Get(A).IsZero &&
            _notCached.Get(B).IsZero &&
            _notCached.Get(C).IsZero &&
            _notCached.Get(D).IsZero;
    }

    [Benchmark(OperationsPerInvoke = 4)]
    public void Set()
    {
        _notCached.Reset(true);

        _notCached.Set(A, ValueA);
        _notCached.Set(B, ValueB);
        _notCached.Set(C, ValueC);
        _notCached.Set(D, ValueD);
    }
}
