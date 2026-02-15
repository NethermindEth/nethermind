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
/// Benchmarks the per-block commit and trie access patterns that dominate block processing time.
/// These help identify which phases benefit most from cross-block caching:
/// - State reads (account lookups)
/// - Storage reads (slot lookups)
/// - Commit cost (flush to trie)
/// - CommitTree cost (trie hashing / merkleization)
///
/// Understanding these breakdowns is critical for targeting the right optimization
/// (Amdahl's law: even perfect caching won't help if commit dominates).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 6)]
public class CommitAndTrieAccessBenchmarks
{
    private WorldState _worldState = null!;
    private Address[] _accounts = null!;
    private BlockHeader _baseBlock = null!;

    [Params(100, 500)]
    public int AccountCount { get; set; }

    [Params(0, 10)]
    public int StorageSlotsPerAccount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        IWorldStateScopeProvider scopeProvider = new TrieStoreScopeProvider(
            TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance),
            new MemDb(),
            LimboLogs.Instance);
        _worldState = new WorldState(scopeProvider, LimboLogs.Instance);

        _accounts = CreateAccounts(AccountCount);

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
            _baseBlock = Build.A.BlockHeader.WithStateRoot(_worldState.StateRoot).TestObject;
        }
    }

    [Benchmark(Baseline = true)]
    public Hash256 ReadAllAccounts()
    {
        using (_worldState.BeginScope(_baseBlock))
        {
            for (int i = 0; i < _accounts.Length; i++)
            {
                _ = _worldState.GetNonce(_accounts[i]);
                _ = _worldState.GetBalance(_accounts[i]);
            }

            return _worldState.StateRoot;
        }
    }

    [Benchmark]
    public Hash256 ReadAllAccountsAndStorage()
    {
        using (_worldState.BeginScope(_baseBlock))
        {
            for (int i = 0; i < _accounts.Length; i++)
            {
                _ = _worldState.GetNonce(_accounts[i]);
                for (int slot = 0; slot < StorageSlotsPerAccount; slot++)
                {
                    _ = _worldState.Get(new StorageCell(_accounts[i], (UInt256)slot));
                }
            }

            return _worldState.StateRoot;
        }
    }

    [Benchmark]
    public Hash256 ModifyAllAccountsAndCommit()
    {
        using (_worldState.BeginScope(_baseBlock))
        {
            for (int i = 0; i < _accounts.Length; i++)
            {
                _worldState.IncrementNonce(_accounts[i], 1);
            }

            _worldState.Commit(Frontier.Instance);
            return _worldState.StateRoot;
        }
    }

    [Benchmark]
    public Hash256 ModifyAllAccountsCommitAndMerkleize()
    {
        using (_worldState.BeginScope(_baseBlock))
        {
            for (int i = 0; i < _accounts.Length; i++)
            {
                _worldState.IncrementNonce(_accounts[i], 1);
            }

            _worldState.Commit(Frontier.Instance);
            _worldState.CommitTree(1);
            return _worldState.StateRoot;
        }
    }

    [Benchmark]
    public Hash256 ModifyStorageAndCommit()
    {
        if (StorageSlotsPerAccount == 0) return Keccak.EmptyTreeHash;

        using (_worldState.BeginScope(_baseBlock))
        {
            for (int i = 0; i < _accounts.Length; i++)
            {
                for (int slot = 0; slot < StorageSlotsPerAccount; slot++)
                {
                    UInt256 newValue = (UInt256)(i * 1000 + slot + 99999);
                    _worldState.Set(new StorageCell(_accounts[i], (UInt256)slot), newValue.ToBigEndian());
                }
            }

            _worldState.Commit(Frontier.Instance);
            _worldState.CommitTree(1);
            return _worldState.StateRoot;
        }
    }

    [Benchmark]
    public Hash256 FullBlockSimulation_ReadModifyCommitMerkleize()
    {
        using (_worldState.BeginScope(_baseBlock))
        {
            // Read all accounts (simulates prewarmer + initial state access)
            for (int i = 0; i < _accounts.Length; i++)
            {
                _ = _worldState.GetNonce(_accounts[i]);
                _ = _worldState.GetBalance(_accounts[i]);
                for (int slot = 0; slot < StorageSlotsPerAccount; slot++)
                {
                    _ = _worldState.Get(new StorageCell(_accounts[i], (UInt256)slot));
                }
            }

            // Modify all accounts (simulates tx execution)
            for (int i = 0; i < _accounts.Length; i++)
            {
                _worldState.IncrementNonce(_accounts[i], 1);
                _worldState.SubtractFromBalance(_accounts[i], UInt256.One, Frontier.Instance);
                for (int slot = 0; slot < StorageSlotsPerAccount; slot++)
                {
                    UInt256 newValue = (UInt256)(i * 1000 + slot + 99999);
                    _worldState.Set(new StorageCell(_accounts[i], (UInt256)slot), newValue.ToBigEndian());
                }
            }

            // Commit and merkleize
            _worldState.Commit(Frontier.Instance);
            _worldState.CommitTree(1);
            return _worldState.StateRoot;
        }
    }

    private static Address[] CreateAccounts(int count)
    {
        Address[] accounts = new Address[count];
        Random random = new(55);
        byte[] buffer = new byte[Address.Size];
        for (int i = 0; i < count; i++)
        {
            random.NextBytes(buffer);
            accounts[i] = new Address((byte[])buffer.Clone());
        }

        return accounts;
    }
}
