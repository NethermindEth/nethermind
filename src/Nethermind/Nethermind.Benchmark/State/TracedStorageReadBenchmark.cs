// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Benchmarks.State;

/// <summary>SLOADs through the EIP-7928 recording decorator within one transaction.</summary>
/// <remarks>Each invocation starts a fresh slice and reads every slot twice, so half the reads record a
/// new slot and half repeat one. <see cref="AccountHasWrite"/> puts a change on the account first.</remarks>
[MemoryDiagnoser]
public class TracedStorageReadBenchmark
{
    private const int Slots = 64;
    private const int OperationsPerInvoke = 2 * Slots;

    private TracedAccessWorldState _state = null!;
    private IDisposable _scope = null!;
    private StorageCell[] _cells = null!;
    private StorageCell _written;

    [Params(false, true)]
    public bool Parallel { get; set; }

    [Params(false, true)]
    public bool AccountHasWrite { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _cells = new StorageCell[Slots];
        for (int i = 0; i < Slots; i++) _cells[i] = new StorageCell(TestItem.AddressA, (UInt256)i);
        _written = new StorageCell(TestItem.AddressA, (UInt256)Slots);

        IWorldState inner = TestWorldStateFactory.CreateForTest();
        Hash256 stateRoot;
        using (inner.BeginScope(IWorldState.PreGenesis))
        {
            inner.CreateAccount(TestItem.AddressA, 0);
            foreach (StorageCell cell in _cells) inner.Set(in cell, (UInt256)7);
            inner.Commit(Amsterdam.Instance, isGenesis: true);
            inner.CommitTree(0);
            stateRoot = inner.StateRoot;
        }

        _state = new TracedAccessWorldState(inner, Parallel);
        _state.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        _scope = _state.BeginScope(Build.A.BlockHeader.WithStateRoot(stateRoot).TestObject);
    }

    [GlobalCleanup]
    public void Cleanup() => _scope.Dispose();

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public ulong ReadEachSlotTwice()
    {
        TracedAccessWorldState state = _state;
        state.Clear();
        if (AccountHasWrite) state.GetGeneratingBlockAccessList()!.AddStorageChange(in _written, UInt256.Zero, UInt256.One);

        ulong n = 0;
        StorageCell[] cells = _cells;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                state.Get(in cells[i], out UInt256 value);
                n += value.u0;
            }
        }

        return n + (ulong)state.GetGeneratingBlockAccessList()!.GetAccountChanges(TestItem.AddressA)!.StorageReads.Count;
    }
}
