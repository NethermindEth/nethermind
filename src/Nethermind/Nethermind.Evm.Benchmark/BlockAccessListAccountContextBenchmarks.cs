// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark;

/// <summary>Measures BAL account lookup locality through world-state reads.</summary>
/// <remarks>All values are declared before the current transaction, excluding parent-state I/O.</remarks>
[MemoryDiagnoser]
public class BlockAccessListAccountContextBenchmarks
{
    private const int ReadCount = 4096;
    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private IDisposable _stateScope = null!;
    private BlockAccessListBasedWorldState _state = null!;
    private Address[] _reads = null!;

    /// <summary>Number of accounts declared in the BAL.</summary>
    [Params(16, 4096)]
    public int AccountCount { get; set; }

    /// <summary>Address locality and object identity of successive reads.</summary>
    [Params("Repeated", "RepeatedEqualInstance", "EqualInstances", "Alternating", "Random")]
    public string Pattern { get; set; } = null!;

    /// <summary>Builds the BAL and deterministic read sequence outside the measured operation.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Amsterdam.Instance))
            .Build();
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        _processingScope = _container.BeginLifetimeScope(builder => builder
            .AddSingleton(scopeProvider)
            .AddSingleton<BlockAccessListBasedWorldState>());
        IWorldState parent = _processingScope.Resolve<IWorldState>();
        _stateScope = parent.BeginScope(IWorldState.PreGenesis);
        _state = _processingScope.Resolve<BlockAccessListBasedWorldState>();

        Address[] addresses = new Address[AccountCount];
        ReadOnlyAccountChanges[] accounts = new ReadOnlyAccountChanges[AccountCount];
        for (int i = 0; i < AccountCount; i++)
        {
            byte[] bytes = new byte[Address.Size];
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(Address.Size - sizeof(int)), i + 1);
            addresses[i] = new Address(bytes);
            accounts[i] = Build.An.AccountChanges.WithAddress(addresses[i])
                .WithNonceChanges(new NonceChange(0, (ulong)i + 1)).TestObject;
        }

        _state.SetParentReader(parent);
        _state.Setup(Build.A.Block.WithBlockAccessList(new ReadOnlyBlockAccessList(accounts, AccountCount)).TestObject);
        _state.SetBlockAccessIndex(1);
        _reads = new Address[ReadCount];
        Random random = new(13311);
        Address repeatedEqualInstance = new(addresses[0].Bytes);
        ulong expected = 0;
        for (int i = 0; i < _reads.Length; i++)
        {
            int index = Pattern switch
            {
                "Repeated" or "RepeatedEqualInstance" or "EqualInstances" => 0,
                "Alternating" => i % 2,
                "Random" => random.Next(AccountCount),
                _ => throw new ArgumentOutOfRangeException(nameof(Pattern))
            };
            _reads[i] = Pattern switch
            {
                "EqualInstances" => new Address(addresses[index].Bytes),
                "RepeatedEqualInstance" => repeatedEqualInstance,
                _ => addresses[index]
            };
            expected += (ulong)index + 1;
        }

        if (ReadNonces() != expected)
            throw new InvalidOperationException("BAL reads did not match the generated sequence.");
    }

    /// <summary>Reads one nonce per address through the shared account-context resolver.</summary>
    [Benchmark(OperationsPerInvoke = ReadCount)]
    public ulong ReadNonces()
    {
        ulong sum = 0;
        foreach (Address address in _reads)
            sum += _state.GetNonce(address);
        return sum;
    }

    /// <summary>Releases the parent scope and DI-owned state infrastructure.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _state.ClearParentReader();
        _stateScope.Dispose();
        _processingScope.Dispose();
        _container.Dispose();
    }
}
