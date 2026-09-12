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
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Benchmarks.State;

/// <summary>A storage read on the BAL validation path, which is what every SLOAD costs from Amsterdam on.</summary>
/// <remarks>Each read resolves the account context, then the declared slot, before it can reach state.
/// A slot declared only as a read is the common case, and is the one that used to pay two probes.</remarks>
[MemoryDiagnoser]
public class BalStorageReadBenchmark
{
    private const int OperationsPerInvoke = 1000;

    /// <summary>Enough declared reads that the account is mapped rather than scanned.</summary>
    private const int DeclaredReads = 64;

    private BlockAccessListBasedWorldState _state = null!;
    private StorageCell _declaredRead;
    private StorageCell _changed;

    [GlobalSetup]
    public void Setup()
    {
        UInt256[] reads = new UInt256[DeclaredReads];
        for (int i = 0; i < DeclaredReads; i++)
        {
            reads[i] = (UInt256)(i + 1);
        }

        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads(reads)
                .WithStorageChanges((UInt256)1000, new StorageChange(0, new UInt256(7)))
                .TestObject)
            .TestObject;

        IWorldState inner = TestWorldStateFactory.CreateForTest();
        Hash256 stateRoot;
        using (inner.BeginScope(IWorldState.PreGenesis))
        {
            inner.CreateAccount(TestItem.AddressA, 1, 1);
            inner.Commit(Cancun.Instance, isGenesis: true);
            inner.CommitTree(0);
            stateRoot = inner.StateRoot;
        }

        BlockHeader baseBlock = Build.A.BlockHeader.WithStateRoot(stateRoot).WithNumber(0).TestObject;
        _state = new BlockAccessListBasedWorldState(inner, LimboLogs.Instance);
        _state.SetBlockAccessIndex(1);
        _state.Setup(Build.A.Block.WithHeader(baseBlock).WithBlockAccessList(bal).TestObject);
        inner.BeginScope(baseBlock);
        _state.SetParentReader(inner);

        _declaredRead = new StorageCell(TestItem.AddressA, (UInt256)32);
        _changed = new StorageCell(TestItem.AddressA, (UInt256)1000);

        _state.Get(_declaredRead);
        _state.Get(_changed);
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke, Baseline = true)]
    public int DeclaredReadSlot()
    {
        int n = 0;
        for (int i = 0; i < OperationsPerInvoke; i++) n += _state.Get(_declaredRead).Length;
        return n;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int ChangedSlot()
    {
        int n = 0;
        for (int i = 0; i < OperationsPerInvoke; i++) n += _state.Get(_changed).Length;
        return n;
    }
}
