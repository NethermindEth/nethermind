// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Benchmarks.State;

/// <summary>A warm SLOAD through the world state, which is what the storage_access_warm benchmarks hit.</summary>
/// <remarks>Reads consult the write journal before the per-contract read cache, so a slot that was never
/// written pays a lookup that cannot hit whenever the contract has written anything at all. The two
/// <c>Unwritten</c> cases differ only in whether that journal is empty.</remarks>
[MemoryDiagnoser]
public class WarmStorageReadBenchmark
{
    private const int OperationsPerInvoke = 1000;

    private IWorldState _cleanJournal = null!;
    private IWorldState _dirtyJournal = null!;
    private IWorldState _otherWritten = null!;
    private StorageCell _unwritten;
    private StorageCell _written;

    private static IWorldState Create(Address address, byte[] value)
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using (IDisposable seed = worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(address, 1, 1);
            for (int i = 0; i < 128; i++)
            {
                worldState.Set(new StorageCell(address, (UInt256)i), value);
            }

            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(0);
        }

        worldState.BeginScope(IWorldState.PreGenesis);
        return worldState;
    }

    [GlobalSetup]
    public void Setup()
    {
        Address address = TestItem.AddressA;
        byte[] value = new byte[32];
        value[31] = 7;

        _unwritten = new StorageCell(address, (UInt256)5);
        _written = new StorageCell(address, (UInt256)99);

        _cleanJournal = Create(address, value);
        _dirtyJournal = Create(address, value);
        _otherWritten = Create(address, value);

        // Same contract: the read cannot skip the journal, because this contract really has entries there.
        _dirtyJournal.Set(_written, value);

        // Another contract entirely: the journal is non-empty, but not for the contract being read.
        _otherWritten.Set(new StorageCell(TestItem.AddressB, (UInt256)1), value);

        _cleanJournal.Get(_unwritten);
        _dirtyJournal.Get(_unwritten);
        _dirtyJournal.Get(_written);
        _otherWritten.Get(_unwritten);
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke, Baseline = true)]
    public int Unwritten_CleanJournal()
    {
        int n = 0;
        for (int i = 0; i < OperationsPerInvoke; i++) n += _cleanJournal.Get(_unwritten).Length;
        return n;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int Unwritten_DirtyJournal()
    {
        int n = 0;
        for (int i = 0; i < OperationsPerInvoke; i++) n += _dirtyJournal.Get(_unwritten).Length;
        return n;
    }

    /// <summary>A read-only contract in a block where a different contract has written.</summary>
    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int Unwritten_OtherContractWritten()
    {
        int n = 0;
        for (int i = 0; i < OperationsPerInvoke; i++) n += _otherWritten.Get(_unwritten).Length;
        return n;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int WrittenSlot()
    {
        int n = 0;
        for (int i = 0; i < OperationsPerInvoke; i++) n += _dirtyJournal.Get(_written).Length;
        return n;
    }
}
