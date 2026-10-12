// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Benchmarks.State;

/// <summary>The transient storage write path, by the shapes the benchmark suite exercises.</summary>
/// <remarks>Each method body resets first — inside the measurement, amortised over
/// <see cref="OperationsPerInvoke"/> writes — so the undo log cannot grow across invocations and the
/// cleanup a transaction boundary causes is counted. Rewriting the same word is the shape a reentrancy
/// guard produces, and the one where other clients skip their journal entry entirely.</remarks>
[MemoryDiagnoser]
public class TransientStorageBenchmark
{
    private const int OperationsPerInvoke = 1000;

    private IWorldState _worldState = null!;
    private StorageCell _fixedCell;
    private StorageCell[] _varyingCells = null!;
    private UInt256 _word;
    private UInt256 _otherWord;

    [GlobalSetup]
    public void Setup()
    {
        _worldState = TestWorldStateFactory.CreateForTest();
        _worldState.BeginScope(IWorldState.PreGenesis);

        _word = (UInt256)7;
        _otherWord = (UInt256)9;

        _fixedCell = new StorageCell(TestItem.AddressA, (UInt256)1);
        _varyingCells = new StorageCell[OperationsPerInvoke];
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            _varyingCells[i] = new StorageCell(TestItem.AddressA, (UInt256)(i + 1000));
        }
    }

    /// <summary>Same cell, same word every time.</summary>
    [Benchmark(OperationsPerInvoke = OperationsPerInvoke, Baseline = true)]
    public void Store_SameWord()
    {
        _worldState.Reset();
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            _worldState.SetTransientState(in _fixedCell, in _word);
        }
    }

    /// <summary>Same cell, alternating words, so every write really changes something.</summary>
    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public void Store_ChangingWord()
    {
        _worldState.Reset();
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            _worldState.SetTransientState(in _fixedCell, (i & 1) == 0 ? _word : _otherWord);
        }
    }

    /// <summary>Different cells, which no journal rule can collapse.</summary>
    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public void Store_VaryingKeys()
    {
        _worldState.Reset();
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            _worldState.SetTransientState(in _varyingCells[i], in _word);
        }
    }
}
