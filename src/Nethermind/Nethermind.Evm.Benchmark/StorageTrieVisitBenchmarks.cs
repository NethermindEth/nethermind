// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Evm.Benchmark;

/// <summary>Measures a full state visit that enters the storage trie of every contract.</summary>
[MemoryDiagnoser]
public class StorageTrieVisitBenchmarks
{
    private const int ContractCount = 1024;

    private IContainer _container = null!;
    private IStateReader _stateReader = null!;
    private BlockHeader _header = null!;

    [Params(false, true)]
    public bool UseFlatDb { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = UseFlatDb }))
            .Build();
        IWorldStateScopeProvider scopeProvider = _container.Resolve<IWorldStateManager>().GlobalWorldState;
        using ILifetimeScope processingScope = _container.BeginLifetimeScope(builder => builder.AddSingleton(scopeProvider));
        IWorldState state = processingScope.Resolve<IWorldState>();
        UInt256 value = 1;
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            for (int i = 0; i < ContractCount; i++)
            {
                Address address = new(Keccak.Compute(Address.FromNumber((UInt256)(ulong)i).Bytes));
                state.CreateAccount(address, UInt256.One);
                state.Set(new StorageCell(address, UInt256.Zero), in value);
            }

            state.Commit(Osaka.Instance);
            state.CommitTree(0);
            _header = Build.A.BlockHeader.WithStateRoot(state.StateRoot).TestObject;
        }

        _stateReader = _container.Resolve<IStateReader>();
        if (Visit() != ContractCount)
        {
            throw new InvalidOperationException("The visit must enter every contract's storage trie.");
        }
    }

    [Benchmark(OperationsPerInvoke = ContractCount)]
    public int Visit()
    {
        StorageLeafCounter counter = new();
        _stateReader.RunTreeVisitor(counter, _header);
        return counter.StorageLeaves;
    }

    [GlobalCleanup]
    public void Cleanup() => _container?.Dispose();

    private sealed class StorageLeafCounter : ITreeVisitor<TreePathContextWithStorage>
    {
        public int StorageLeaves { get; private set; }

        public bool IsFullDbScan => false;

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) => true;

        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }

        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) =>
            throw new InvalidOperationException($"Missing node {nodeHash}");

        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) { }

        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) { }

        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node)
        {
            if (nodeContext.Storage is not null) StorageLeaves++;
        }

        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) { }
    }
}
