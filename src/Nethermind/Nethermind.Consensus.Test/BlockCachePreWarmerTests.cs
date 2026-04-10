// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Concurrent;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

[TestFixture]
public class BlockCachePreWarmerTests
{
    private IContainer _container;
    private ILifetimeScope _processingScope;
    private Nethermind.Core.Crypto.Hash256 _genesisStateRoot;

    [SetUp]
    public void Setup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .Build();

        IMainProcessingModule[] mainModules = _container.Resolve<IMainProcessingModule[]>();
        IWorldStateManager wsm = _container.Resolve<IWorldStateManager>();
        IWorldStateScopeProvider scopeProvider = wsm.GlobalWorldState;

        _processingScope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(wsm).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(mainModules);
        });

        // Seed genesis state for all sender accounts and capture the root while still in scope.
        IWorldState worldState = _processingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 1_000_000.Ether);
            worldState.CreateAccount(TestItem.AddressB, 1_000_000.Ether);
            worldState.Commit(Osaka.Instance);
            worldState.CommitTree(0);
            _genesisStateRoot = worldState.StateRoot;
        }
    }

    [TearDown]
    public void TearDown()
    {
        _processingScope?.Dispose();
        _container?.Dispose();
    }

    /// <summary>
    /// Verifies that envs evicted from the pool during real block processing are disposed
    /// immediately. With pool capacity 1 and two parallel sender groups, the second worker
    /// to return its env triggers eviction — that env must be disposed on the spot.
    /// </summary>
    [Test]
    public async Task PreWarmCaches_WhenPoolEvicts_EvictedEnvsAreDisposed()
    {
        (BlockCachePreWarmer preWarmer, ConcurrentBag<IReadOnlyTxProcessorSource> created,
            ConcurrentBag<IReadOnlyTxProcessorSource> disposed) = CreatePreWarmer(maxPoolSize: 1);

        await preWarmer.PreWarmCaches(BuildTwoSenderBlock(), BuildParentHeader(), Osaka.Instance);

        // With pool capacity 1 and two parallel workers, at least one eviction must occur.
        created.Count.Should().BeGreaterThanOrEqualTo(2,
            "two distinct senders must have exercised two concurrent workers");
        int evictedCount = created.Count - 1; // at most 1 retained in pool
        disposed.Count.Should().BeGreaterThanOrEqualTo(evictedCount,
            "all envs evicted from the pool must have Dispose() called immediately");
    }

    /// <summary>
    /// Verifies that envs retained in the pool are disposed when the prewarmer itself is
    /// disposed, releasing their child LifetimeScopes at processing-scope shutdown rather
    /// than waiting for the parent scope to close.
    /// </summary>
    [Test]
    public async Task Dispose_WhenCalled_DisposesRetainedEnvsInPool()
    {
        (BlockCachePreWarmer preWarmer, ConcurrentBag<IReadOnlyTxProcessorSource> created,
            ConcurrentBag<IReadOnlyTxProcessorSource> disposed) = CreatePreWarmer(maxPoolSize: 10);

        await preWarmer.PreWarmCaches(BuildTwoSenderBlock(), BuildParentHeader(), Osaka.Instance);

        disposed.Count.Should().Be(0, "no eviction should have occurred with a large pool");
        created.Count.Should().BeGreaterThanOrEqualTo(1, "at least one env must have been created");

        preWarmer.Dispose();

        disposed.Count.Should().Be(created.Count,
            "all retained envs must be disposed when the prewarmer is disposed");
    }

    private (BlockCachePreWarmer, ConcurrentBag<IReadOnlyTxProcessorSource> created, ConcurrentBag<IReadOnlyTxProcessorSource> disposed) CreatePreWarmer(int maxPoolSize)
    {
        PrewarmerEnvFactory envFactory = _processingScope.Resolve<PrewarmerEnvFactory>();
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        NodeStorageCache nodeStorageCache = _processingScope.Resolve<NodeStorageCache>();

        ConcurrentBag<IReadOnlyTxProcessorSource> created = [];
        ConcurrentBag<IReadOnlyTxProcessorSource> disposed = [];
        DisposalTrackingPolicy trackingPolicy = new(envFactory, preBlockCaches, created, disposed);

        BlockCachePreWarmer preWarmer = new(
            trackingPolicy,
            maxPoolSize: maxPoolSize,
            concurrency: 2,
            nodeStorageCache,
            preBlockCaches,
            LimboLogs.Instance);

        return (preWarmer, created, disposed);
    }

    private BlockHeader BuildParentHeader() =>
        Build.A.BlockHeader
            .WithNumber(0)
            .WithStateRoot(_genesisStateRoot)
            .WithGasLimit(30_000_000)
            .TestObject;

    /// <summary>
    /// Builds a block with transactions from two distinct senders, producing two parallel
    /// sender groups in <c>WarmupTransactions</c> and guaranteeing concurrent pool usage.
    /// </summary>
    private static Block BuildTwoSenderBlock()
    {
        Transaction[] txs =
        [
            Build.A.Transaction.WithNonce(0).WithTo(TestItem.AddressC).WithValue(1.Wei).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
            Build.A.Transaction.WithNonce(0).WithTo(TestItem.AddressC).WithValue(1.Wei).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
        ];

        return Build.A.Block
            .WithTransactions(txs)
            .WithGasLimit(30_000_000)
            .TestObject;
    }

    /// <summary>
    /// Pool policy that delegates to <see cref="PrewarmerEnvFactory"/> and records
    /// every env created and every env disposed via <see cref="TrackingEnv"/> wrappers.
    /// </summary>
    private sealed class DisposalTrackingPolicy(
        PrewarmerEnvFactory factory,
        PreBlockCaches caches,
        ConcurrentBag<IReadOnlyTxProcessorSource> created,
        ConcurrentBag<IReadOnlyTxProcessorSource> disposed)
        : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create()
        {
            TrackingEnv env = new(factory.Create(caches), disposed);
            created.Add(env);
            return env;
        }

        public bool Return(IReadOnlyTxProcessorSource obj) => true;

        /// <summary>
        /// Wraps an inner env and records itself in <paramref name="disposed"/> when
        /// <see cref="Dispose"/> is called, allowing the test to distinguish envs
        /// disposed by pool eviction from those still retained.
        /// </summary>
        private sealed class TrackingEnv(
            IReadOnlyTxProcessorSource inner,
            ConcurrentBag<IReadOnlyTxProcessorSource> disposed)
            : IReadOnlyTxProcessorSource
        {
            public IReadOnlyTxProcessingScope Build(BlockHeader? baseBlock) =>
                inner.Build(baseBlock);

            public void Dispose()
            {
                disposed.Add(this);
                inner.Dispose();
            }
        }
    }
}
