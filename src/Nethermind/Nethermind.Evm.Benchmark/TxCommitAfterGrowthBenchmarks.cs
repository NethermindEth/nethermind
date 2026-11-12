// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark;

/// <summary>Measures the per-transaction world-state round of small transactions after one heavy transaction.</summary>
/// <remarks>
/// The per-transaction maps keep the capacity of the largest transaction they held, so a node pays for its
/// heaviest transaction on every later commit. <see cref="HeavyTxEntries"/> 0 is the control without that history.
/// </remarks>
[MemoryDiagnoser]
public class TxCommitAfterGrowthBenchmarks
{
    private const int Transactions = 1_000;
    private const int AccountsPerTx = 6;
    private const int SlotsPerTx = 4;

    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private IDisposable _stateScope = null!;
    private IWorldState _state = null!;
    private Address[] _accounts = null!;
    private StorageCell[] _cells = null!;
    private readonly UInt256 _one = UInt256.One;
    private readonly UInt256 _two = 2;
    private bool _alternate;

    [Params(0, 4096)]
    public int HeavyTxEntries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .Build();
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        _processingScope = _container.BeginLifetimeScope(builder => builder.AddSingleton(scopeProvider));
        _state = _processingScope.Resolve<IWorldState>();
        _stateScope = _state.BeginScope(IWorldState.PreGenesis);

        int accountCount = Math.Max(HeavyTxEntries, AccountsPerTx);
        _accounts = new Address[accountCount];
        for (int i = 0; i < accountCount; i++)
        {
            _accounts[i] = Address.FromNumber((UInt256)(i + 1));
            _state.CreateAccount(_accounts[i], UInt256.One);
        }

        int cellCount = Math.Max(HeavyTxEntries, SlotsPerTx);
        _cells = new StorageCell[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            _cells[i] = new StorageCell(_accounts[0], (UInt256)i);
            _state.Set(_cells[i], _one);
        }

        _state.Commit(Osaka.Instance, NullStateTracer.Instance, commitRoots: false);

        if (HeavyTxEntries > 0)
        {
            _state.TakeSnapshot(newTransactionStart: true);
            for (int i = 0; i < HeavyTxEntries; i++)
            {
                _ = _state.GetBalance(_accounts[i]);
                _state.Set(_cells[i], _two);
            }

            _state.Commit(Osaka.Instance, NullStateTracer.Instance, commitRoots: false);
        }
    }

    [Benchmark(OperationsPerInvoke = Transactions)]
    public void SmallTxCommits()
    {
        for (int tx = 0; tx < Transactions; tx++)
        {
            _alternate = !_alternate;
            UInt256 value = _alternate ? _two : _one;
            _state.TakeSnapshot(newTransactionStart: true);
            for (int i = 0; i < AccountsPerTx; i++)
            {
                _ = _state.GetBalance(_accounts[i]);
            }

            _state.IncrementNonce(_accounts[0], 1);
            _state.AddToBalance(_accounts[1], in _one, Osaka.Instance);
            for (int i = 0; i < SlotsPerTx; i++)
            {
                _state.Get(in _cells[i], out _);
                _state.Set(in _cells[i], in value);
            }

            _state.Commit(Osaka.Instance, NullStateTracer.Instance, commitRoots: false);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stateScope.Dispose();
        _processingScope.Dispose();
        _container.Dispose();
    }
}
