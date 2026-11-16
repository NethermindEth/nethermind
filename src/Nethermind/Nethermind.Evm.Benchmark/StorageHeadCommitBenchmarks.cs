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

/// <summary>Per-transaction storage commits, after a transaction whose reverted writes grew the per-transaction cell map.</summary>
[MemoryDiagnoser]
public class StorageHeadCommitBenchmarks
{
    private const int Transactions = 1000;

    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private IDisposable _stateScope = null!;
    private IWorldState _state = null!;
    private StorageCell[] _cells = null!;
    private UInt256 _value = UInt256.One;

    [Params(0, 2048)]
    public int WideTxCells { get; set; }

    [Params(4, 64)]
    public int CellsPerTx { get; set; }

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
        _state.CreateAccount(Address.Zero, UInt256.One);
        _state.Commit(Osaka.Instance);

        Snapshot beforeWideWrites = _state.TakeSnapshot();
        for (int i = 0; i < WideTxCells; i++)
        {
            _state.Set(new StorageCell(Address.Zero, (UInt256)(ulong)(1_000_000 + i)), UInt256.One);
        }
        _state.Restore(beforeWideWrites);
        _state.Set(new StorageCell(Address.Zero, (UInt256)(ulong)999_999), UInt256.One);
        _state.Commit(Osaka.Instance, NullStateTracer.Instance, commitRoots: false);

        _cells = new StorageCell[CellsPerTx];
        for (int i = 0; i < CellsPerTx; i++)
        {
            _cells[i] = new StorageCell(Address.Zero, (UInt256)(ulong)i);
        }
    }

    [Benchmark(OperationsPerInvoke = Transactions)]
    public void NarrowTxCommits()
    {
        StorageCell[] cells = _cells;
        for (int tx = 0; tx < Transactions; tx++)
        {
            _value += UInt256.One;
            for (int i = 0; i < cells.Length; i++)
            {
                _state.Set(cells[i], _value);
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
