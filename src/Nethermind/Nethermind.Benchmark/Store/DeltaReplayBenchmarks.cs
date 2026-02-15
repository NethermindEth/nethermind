// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Benchmarks the ApplyBlockDeltasToWarmCache method that replays committed block
/// deltas into the cross-block PreBlockCaches. Measures epoch-clear + rebuild cost
/// for state cache and incremental update cost for storage cache.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 6)]
public class DeltaReplayBenchmarks
{
    private WorldState _worldState = null!;
    private PreBlockCaches _preBlockCaches = null!;
    private BlockHeader _baseBlockHeader = null!;
    private Address[] _accounts = null!;

    [Params(10, 100, 1000)]
    public int ModifiedAccountCount { get; set; }

    [Params(0, 5, 50)]
    public int StorageSlotsPerAccount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _preBlockCaches = new PreBlockCaches();
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(
            TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
            new MemDb(),
            LimboLogs.Instance);
        scopeProvider = new PrewarmerScopeProvider(scopeProvider, _preBlockCaches, populatePreBlockCache: false);
        _worldState = new WorldState(scopeProvider, LimboLogs.Instance);

        _accounts = CreateAccounts(ModifiedAccountCount);

        // Create all accounts in genesis
        using (_worldState.BeginScope(IWorldState.PreGenesis))
        {
            for (int i = 0; i < _accounts.Length; i++)
            {
                _worldState.CreateAccount(_accounts[i], (UInt256)(i + 1000));
                for (int slot = 0; slot < StorageSlotsPerAccount; slot++)
                {
                    UInt256 value = (UInt256)(i * 1000 + slot + 1);
                    _worldState.Set(new StorageCell(_accounts[i], (UInt256)slot), value.ToBigEndian());
                }
            }

            _worldState.Commit(Frontier.Instance);
            _worldState.CommitTree(0);
            _baseBlockHeader = Build.A.BlockHeader.WithStateRoot(_worldState.StateRoot).TestObject;
            _worldState.ApplyBlockDeltasToWarmCache();
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Re-open scope and apply modifications that will be committed
        _worldState.BeginScope(_baseBlockHeader);

        for (int i = 0; i < _accounts.Length; i++)
        {
            _worldState.IncrementNonce(_accounts[i], 1);
            for (int slot = 0; slot < StorageSlotsPerAccount; slot++)
            {
                UInt256 newValue = (UInt256)(i * 1000 + slot + 99999);
                _worldState.Set(new StorageCell(_accounts[i], (UInt256)slot), newValue.ToBigEndian());
            }
        }

        _worldState.Commit(Frontier.Instance);
    }

    [Benchmark]
    public void ApplyBlockDeltas()
    {
        _worldState.ApplyBlockDeltasToWarmCache();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        // Dispose the scope we opened in IterationSetup
        // Reset state for next iteration
    }

    private static Address[] CreateAccounts(int count)
    {
        Address[] accounts = new Address[count];
        Random random = new(73);
        byte[] buffer = new byte[Address.Size];

        for (int i = 0; i < count; i++)
        {
            random.NextBytes(buffer);
            accounts[i] = new Address((byte[])buffer.Clone());
        }

        return accounts;
    }
}
