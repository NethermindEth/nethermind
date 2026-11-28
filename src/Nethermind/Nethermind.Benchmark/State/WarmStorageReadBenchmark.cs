// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Benchmarks.State;

/// <summary>A warm SLOAD through the world state, which is what the storage_access_warm benchmarks hit.</summary>
/// <remarks>Reads consult the write journal before the per-contract read cache, so a slot that was never
/// written pays a lookup that cannot hit whenever the contract has written anything at all. The two
/// <c>Unwritten</c> cases differ only in whether that journal is empty; compare rows to the
/// <c>Unwritten_CleanJournal</c> baseline within a run rather than across runs. The read targets are
/// seeded in a committed block and the measurement scope reopens that root, so the contracts' journal
/// flags start genuinely clear — a same-scope seed would mark them and measure the wrong branch.</remarks>
[MemoryDiagnoser]
public class WarmStorageReadBenchmark
{
    private const int OperationsPerInvoke = 1000;

    private sealed class Env : IDisposable
    {
        public required IContainer Container { get; init; }
        public required IWorldState WorldState { get; init; }
        public required IDisposable Scope { get; init; }

        public void Dispose()
        {
            Scope.Dispose();
            Container.Dispose();
        }
    }

    private Env _cleanJournal = null!;
    private Env _dirtyJournal = null!;
    private Env _otherWritten = null!;
    private Env _alternating = null!;
    private StorageCell _unwritten;
    private StorageCell _written;
    private StorageCell _otherContractWritten;
    private static readonly UInt256 Value = (UInt256)7;

    private Env Create(in StorageCell probe)
    {
        IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new BlocksConfig { PreWarming = PreWarmMode.None }))
            .Build();

        IWorldState worldState = container.Resolve<IMainProcessingContext>().WorldState;
        BlockHeader baseBlock;
        using (IDisposable seed = worldState.BeginScope(IWorldState.PreGenesis))
        {
            foreach (Address address in (Address[])[TestItem.AddressA, TestItem.AddressB])
            {
                worldState.CreateAccount(address, 1, 1);
                for (int i = 0; i < 128; i++)
                {
                    worldState.Set(new StorageCell(address, (UInt256)i), in Value);
                }
            }

            worldState.Commit(Frontier.Instance);
            worldState.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(worldState.StateRoot).TestObject;
        }

        IDisposable scope = worldState.BeginScope(baseBlock);
        // The measurement is meaningless against an empty root, so refuse to run rather than report it.
        // The probe cell is a parameter so each call site shows which value the guard checks, rather
        // than the guard reaching for a field the caller has to remember to assign first.
        worldState.Get(in probe, out UInt256 seeded);
        if (seeded != Value)
        {
            throw new InvalidOperationException("The measurement scope does not see the seeded storage.");
        }

        return new Env { Container = container, WorldState = worldState, Scope = scope };
    }

    [GlobalSetup]
    public void Setup()
    {
        _unwritten = new StorageCell(TestItem.AddressA, (UInt256)5);
        _written = new StorageCell(TestItem.AddressA, (UInt256)99);
        _otherContractWritten = new StorageCell(TestItem.AddressB, (UInt256)1);

        _cleanJournal = Create(in _unwritten);
        _dirtyJournal = Create(in _unwritten);
        _otherWritten = Create(in _unwritten);
        _alternating = Create(in _unwritten);

        // Same contract: the read cannot skip the journal, because this contract really has entries there.
        _dirtyJournal.WorldState.Set(in _written, in Value);

        // Another contract entirely: the journal is non-empty, but not for the contract being read.
        _otherWritten.WorldState.Set(in _otherContractWritten, in Value);

        // Both contracts journalled: alternating journal hits defeat the last-contract memo, so every
        // read pays the contract-map probe the gate adds in front of the journal probe.
        _alternating.WorldState.Set(in _written, in Value);
        _alternating.WorldState.Set(in _otherContractWritten, in Value);

        _cleanJournal.WorldState.Get(in _unwritten, out _);
        _dirtyJournal.WorldState.Get(in _unwritten, out _);
        _dirtyJournal.WorldState.Get(in _written, out _);
        _otherWritten.WorldState.Get(in _unwritten, out _);
        _alternating.WorldState.Get(in _written, out _);
        _alternating.WorldState.Get(in _otherContractWritten, out _);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        // Null-conditional: if a Create() guard fired mid-Setup, the later fields are still null and a
        // bare Dispose here would replace the real "empty root" error with an NRE.
        _cleanJournal?.Dispose();
        _dirtyJournal?.Dispose();
        _otherWritten?.Dispose();
        _alternating?.Dispose();
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke, Baseline = true)]
    public ulong Unwritten_CleanJournal() => Read(_cleanJournal.WorldState, in _unwritten);

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public ulong Unwritten_DirtyJournal() => Read(_dirtyJournal.WorldState, in _unwritten);

    /// <summary>A read-only contract in a block where a different contract has written.</summary>
    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public ulong Unwritten_OtherContractWritten() => Read(_otherWritten.WorldState, in _unwritten);

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public ulong WrittenSlot() => Read(_dirtyJournal.WorldState, in _written);

    /// <summary>Journal hits alternating between two written contracts, the shape of a CALL reading a
    /// slot its caller wrote: the last-contract memo misses on every read.</summary>
    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public ulong WrittenSlot_AlternatingContracts()
    {
        ulong n = 0;
        IWorldState worldState = _alternating.WorldState;
        for (int i = 0; i < OperationsPerInvoke / 2; i++)
        {
            worldState.Get(in _written, out UInt256 first);
            worldState.Get(in _otherContractWritten, out UInt256 second);
            n += first.u0 + second.u0;
        }

        return n;
    }

    private static ulong Read(IWorldState worldState, in StorageCell cell)
    {
        ulong n = 0;
        for (int i = 0; i < OperationsPerInvoke; i++)
        {
            worldState.Get(in cell, out UInt256 value);
            n += value.u0;
        }

        return n;
    }
}
