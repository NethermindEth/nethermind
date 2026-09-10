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

/// <summary>Measures cached storage reads, journal updates, rollback and transaction commit.</summary>
[MemoryDiagnoser]
public class StorageJournalBenchmarks
{
    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private IDisposable _stateScope = null!;
    private IWorldState _state = null!;
    private StorageCell[] _cells = null!;
    private StorageCell[] _freshCells = null!;
    private readonly byte[] _initial = [1];
    private readonly byte[] _updated = [2];
    private bool _alternate;

    [Params(128, 4096)]
    public int SlotCount { get; set; }

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
        _cells = new StorageCell[SlotCount];
        _freshCells = new StorageCell[SlotCount];
        for (int i = 0; i < SlotCount; i++)
        {
            _cells[i] = new StorageCell(Address.Zero, (UInt256)i);
            _freshCells[i] = new StorageCell(Address.Zero, (UInt256)(SlotCount + i));
            _state.Set(_cells[i], _initial);
        }
        _state.Commit(Osaka.Instance, NullStateTracer.Instance, commitRoots: false);
    }

    [Benchmark]
    public int CachedReads()
    {
        int sum = 0;
        for (int pass = 0; pass < 8; pass++)
            foreach (StorageCell cell in _cells)
                sum += _state.Get(cell)[0];
        return sum;
    }

    [Benchmark]
    public void RepeatedWritesAndRestore()
    {
        Snapshot snapshot = _state.TakeSnapshot();
        for (int pass = 0; pass < 8; pass++)
            foreach (StorageCell cell in _cells)
                _state.Set(cell, _updated);
        _state.Restore(snapshot);
    }

    [Benchmark]
    public void FreshWritesAndRestore()
    {
        Snapshot snapshot = _state.TakeSnapshot();
        foreach (StorageCell cell in _freshCells)
            _state.Set(cell, _updated);
        _state.Restore(snapshot);
    }

    [Benchmark]
    public void WriteAndCommit() => WriteAndCommit(1);

    [Benchmark]
    public void RepeatedWritesAndCommit() => WriteAndCommit(8);

    [Benchmark]
    public void RepeatedWritesRestoringOriginalAndCommit() => WriteAndCommit(8, restoreOriginal: true);

    private void WriteAndCommit(int passes, bool restoreOriginal = false)
    {
        if (restoreOriginal)
            foreach (StorageCell cell in _cells)
                _state.Get(cell);

        _alternate = !_alternate;
        byte[] value = restoreOriginal || _alternate ? _updated : _initial;
        for (int pass = 0; pass < passes; pass++)
            foreach (StorageCell cell in _cells)
                _state.Set(cell, value);
        if (restoreOriginal)
            foreach (StorageCell cell in _cells)
                _state.Set(cell, _initial);
        _state.Commit(Osaka.Instance, NullStateTracer.Instance, commitRoots: false);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _stateScope.Dispose();
        _processingScope.Dispose();
        _container.Dispose();
    }
}
