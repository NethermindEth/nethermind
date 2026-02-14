// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Benchmarks cross-block PreBlockCache reuse for the prewarmer pipeline.
///
/// In production, the prewarmer populates PreBlockCaches (SeqlockCache) while running
/// ahead of the main processing thread. Cross-block cache reuse keeps state/storage
/// caches warm between blocks, so the prewarmer for block N+1 gets cache hits on
/// accounts/storage that were accessed in block N.
///
/// This benchmark measures the prewarmer's read throughput with cold vs warm caches.
/// </summary>
[MemoryDiagnoser]
public class PreBlockCacheReuseBenchmarks
{
    private const int AccountCount = 1024;
    private const int ContractCount = 128;
    private const int SlotCount = ContractCount * 32;

    private IWorldState _prewarmerWs;
    private IWorldState _directTrieWs;
    private PreBlockCaches _preBlockCaches;
    private Address[] _accounts;
    private (Address Account, UInt256 Slot)[] _slots;
    private BlockHeader _baseBlock;
    private readonly IReleaseSpec _spec = new Prague();

    [GlobalSetup]
    public void Setup()
    {
        IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateManager wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);

        // Writable world state for initial population
        IWorldState mainWs = new WorldState(wsm.GlobalWorldState, LimboLogs.Instance);
        using (mainWs.BeginScope(IWorldState.PreGenesis))
        {
            Random rand = new(42);
            byte[] buf = new byte[20];

            // Create accounts
            _accounts = new Address[AccountCount];
            for (int i = 0; i < AccountCount; i++)
            {
                rand.NextBytes(buf);
                Address addr = new Address(buf.ToArray());
                mainWs.AddToBalanceAndCreateIfNotExists(addr, (UInt256)rand.NextInt64() + 1, _spec);
                _accounts[i] = addr;
            }

            // Create contracts with storage
            Address[] contracts = new Address[ContractCount];
            for (int i = 0; i < ContractCount; i++)
            {
                rand.NextBytes(buf);
                Address addr = new Address(buf.ToArray());
                mainWs.AddToBalanceAndCreateIfNotExists(addr, (UInt256)rand.NextInt64() + 1, _spec);
                contracts[i] = addr;
            }

            _slots = new (Address, UInt256)[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                Address contract = contracts[rand.Next(ContractCount)];
                UInt256 slot = (UInt256)rand.NextInt64();
                rand.NextBytes(buf);
                mainWs.Set(new StorageCell(contract, slot), buf.ToArray());
                _slots[i] = (contract, slot);
            }

            mainWs.Commit(_spec);
            mainWs.CommitTree(0);
            _baseBlock = Build.A.BlockHeader.WithStateRoot(mainWs.StateRoot).TestObject;
        }

        // Direct trie world state (no PreBlockCaches layer) for baseline
        _directTrieWs = new WorldState(wsm.CreateResettableWorldState(), LimboLogs.Instance);

        // Prewarmer world state with PreBlockCaches
        _preBlockCaches = new PreBlockCaches();
        PrewarmerScopeProvider prewarmerScope = new PrewarmerScopeProvider(
            wsm.CreateResettableWorldState(),
            _preBlockCaches,
            populatePreBlockCache: true);
        _prewarmerWs = new WorldState(prewarmerScope, LimboLogs.Instance);
    }

    /// <summary>
    /// Baseline: reads go directly through the trie without any PreBlockCaches layer.
    /// This measures pure trie traversal cost (in-memory DB).
    /// </summary>
    [Benchmark]
    public void DirectTrieReads()
    {
        using IDisposable _ = _directTrieWs.BeginScope(_baseBlock);
        ReadAccounts(_directTrieWs);
        ReadStorage(_directTrieWs);
    }

    /// <summary>
    /// Old behavior: PreBlockCaches are cleared between blocks.
    /// Block 1 populates the cache; block 2 starts cold (all cache misses → trie).
    /// </summary>
    [Benchmark(Baseline = true)]
    public void CrossBlock_ClearAllCaches()
    {
        // Block 1: read all state (populates PreBlockCaches via GetOrAdd)
        _preBlockCaches.ClearCaches();
        ReadBlock();

        // Between blocks: clear all caches (old behavior)
        _preBlockCaches.ClearCaches();

        // Block 2: read same state (all cache misses → goes to trie)
        ReadBlock();
    }

    /// <summary>
    /// New behavior: only precompile cache is cleared between blocks.
    /// State/storage caches are kept warm from block 1.
    /// Block 2 gets cache hits on overlapping state.
    /// </summary>
    [Benchmark]
    public void CrossBlock_KeepCachesWarm()
    {
        // Block 1: read all state (populates PreBlockCaches via GetOrAdd)
        _preBlockCaches.ClearCaches();
        ReadBlock();

        // Between blocks: only clear precompile cache (new behavior)
        _preBlockCaches.ClearPrecompileOnly();

        // Block 2: read same state (cache hits from block 1!)
        ReadBlock();
    }

    private void ReadBlock()
    {
        using IDisposable _ = _prewarmerWs.BeginScope(_baseBlock);
        ReadAccounts(_prewarmerWs);
        ReadStorage(_prewarmerWs);
    }

    private void ReadAccounts(IWorldState ws)
    {
        for (int i = 0; i < _accounts.Length; i++)
        {
            ws.GetBalance(_accounts[i]);
        }
    }

    private void ReadStorage(IWorldState ws)
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            ws.Get(new StorageCell(_slots[i].Account, _slots[i].Slot));
        }
    }
}

/// <summary>
/// Measures UpdatePreBlockCaches overhead — the cost of capturing committed
/// state/storage changes and writing them to PreBlockCaches after each block.
/// </summary>
[MemoryDiagnoser]
public class UpdatePreBlockCachesBenchmarks
{
    private IWorldState _worldState;
    private Address[] _accounts;
    private (Address Account, UInt256 Slot)[] _slots;
    private BlockHeader _baseBlock;
    private readonly IReleaseSpec _spec = new Prague();

    [Params(100, 500, 2000)]
    public int ModifiedAccountCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        IDbProvider dbProvider = TestMemDbProvider.Init();
        WorldStateManager wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);

        PreBlockCaches preBlockCaches = new PreBlockCaches();
        PrewarmerScopeProvider scopeProvider = new PrewarmerScopeProvider(
            wsm.GlobalWorldState,
            preBlockCaches,
            populatePreBlockCache: false);
        _worldState = new WorldState(scopeProvider, LimboLogs.Instance);

        // Create initial state
        using (_worldState.BeginScope(IWorldState.PreGenesis))
        {
            Random rand = new(42);
            byte[] buf = new byte[20];

            int maxAccounts = 2000;
            _accounts = new Address[maxAccounts];
            for (int i = 0; i < maxAccounts; i++)
            {
                rand.NextBytes(buf);
                Address addr = new Address(buf.ToArray());
                _worldState.AddToBalanceAndCreateIfNotExists(addr, (UInt256)rand.NextInt64() + 1, _spec);
                _accounts[i] = addr;
            }

            _slots = new (Address, UInt256)[maxAccounts * 4];
            for (int i = 0; i < _slots.Length; i++)
            {
                Address contract = _accounts[rand.Next(128)];
                UInt256 slot = (UInt256)rand.NextInt64();
                rand.NextBytes(buf);
                _worldState.Set(new StorageCell(contract, slot), buf.ToArray());
                _slots[i] = (contract, slot);
            }

            _worldState.Commit(_spec);
            _worldState.CommitTree(0);
            _baseBlock = Build.A.BlockHeader.WithStateRoot(_worldState.StateRoot).TestObject;
        }
    }

    [Benchmark]
    public void CommitAndUpdatePreBlockCaches()
    {
        using IDisposable _ = _worldState.BeginScope(_baseBlock);

        // Simulate block processing: modify N accounts
        for (int i = 0; i < ModifiedAccountCount; i++)
        {
            _worldState.AddToBalance(_accounts[i], 1, _spec);
        }

        // Simulate storage writes (4 slots per modified account, capped)
        int storageWrites = Math.Min(ModifiedAccountCount * 4, _slots.Length);
        byte[] buf = new byte[32];
        for (int i = 0; i < storageWrites; i++)
        {
            _worldState.Set(new StorageCell(_slots[i].Account, _slots[i].Slot), buf);
        }

        _worldState.Commit(_spec);
        _worldState.CommitTree(1);

        // Measure the cost of capturing changes for cache reuse
        _worldState.UpdatePreBlockCaches();
    }
}
