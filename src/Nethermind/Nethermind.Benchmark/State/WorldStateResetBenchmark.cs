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
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// A world state reset after <see cref="Touched"/> accounts were read and as many slots written, the work a call scope
/// does before it closes and block processing does at the end of a block.
/// </summary>
/// <remarks>The reads and writes are part of each operation, so compare the same row across runs, not rows within one.</remarks>
[MemoryDiagnoser]
[ProcessCount(6)]
[IterationCount(20)]
public class WorldStateResetBenchmark
{
    private static readonly UInt256 Value = 7;

    private IContainer _container = null!;
    private IWorldState _worldState = null!;
    private IDisposable _scope = null!;
    private Address[] _addresses = null!;

    [Params(4, 32, 256, 4096)]
    public int Touched { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new BlocksConfig { PreWarming = PreWarmMode.None }, new FlatDbConfig { Enabled = true }))
            .Build();
        _worldState = _container.Resolve<IMainProcessingContext>().WorldState;

        _addresses = new Address[Touched];
        for (int i = 0; i < Touched; i++) _addresses[i] = Address.FromNumber((UInt256)(ulong)(i + 1));

        BlockHeader baseBlock;
        using (IDisposable seed = _worldState.BeginScope(IWorldState.PreGenesis))
        {
            foreach (Address address in _addresses) _worldState.CreateAccount(address, 1, 1);
            _worldState.Commit(Frontier.Instance);
            _worldState.CommitTree(0);
            baseBlock = Build.A.BlockHeader.WithStateRoot(_worldState.StateRoot).TestObject;
        }

        _scope = _worldState.BeginScope(baseBlock);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scope?.Dispose();
        _container?.Dispose();
    }

    [Benchmark]
    public void TouchAndReset()
    {
        IWorldState worldState = _worldState;
        Address[] addresses = _addresses;
        for (int i = 0; i < addresses.Length; i++)
        {
            worldState.GetNonce(addresses[i]);
            worldState.Set(new StorageCell(addresses[i], UInt256.One), in Value);
        }

        worldState.Reset();
    }
}
