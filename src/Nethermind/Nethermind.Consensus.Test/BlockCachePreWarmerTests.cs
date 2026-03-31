// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

/// <summary>
/// Regression tests for the pool eviction leak in <see cref="BlockCachePreWarmer"/>.
///
/// <see cref="DefaultObjectPool{T}"/> silently drops objects when at capacity without
/// calling <see cref="IDisposable.Dispose"/>. The child <see cref="ILifetimeScope"/>
/// inside each evicted env was never closed, leaking <see cref="Nethermind.State.WorldState"/>
/// and everything it holds until process shutdown.
///
/// Fix: <see cref="IReadOnlyTxProcessorSource"/> now extends <see cref="IDisposable"/>,
/// and <see cref="DefaultObjectPoolProvider"/> creates a <c>DisposableObjectPool</c> that
/// calls <see cref="IDisposable.Dispose"/> on any item it cannot retain.
/// </summary>
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
    /// Verifies that envs evicted from the prewarmer pool during real block processing
    /// are disposed immediately, not leaked to the GC.
    ///
    /// The test calls <see cref="BlockCachePreWarmer.PreWarmCaches"/> with a block that has
    /// transactions from two distinct senders. <c>WarmupTransactions</c> groups by sender,
    /// so two parallel workers each acquire an env from the pool. With pool capacity 1, the
    /// second worker to return its env triggers eviction — that env must be disposed.
    ///
    /// A <see cref="DisposalTrackingPolicy"/> wraps <see cref="PrewarmerEnvFactory"/> so
    /// the test can assert that <see cref="IDisposable.Dispose"/> was called on every env
    /// the pool could not retain.
    /// </summary>
    [Test]
    public async Task PreWarmCaches_WhenPoolEvicts_EvictedEnvsAreDisposed()
    {
        PrewarmerEnvFactory envFactory = _processingScope.Resolve<PrewarmerEnvFactory>();
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        NodeStorageCache nodeStorageCache = _processingScope.Resolve<NodeStorageCache>();

        ConcurrentBag<IReadOnlyTxProcessorSource> created = new();
        ConcurrentBag<IReadOnlyTxProcessorSource> disposed = new();
        DisposalTrackingPolicy trackingPolicy = new(envFactory, preBlockCaches, created, disposed);

        // Pool capacity 1 — two sender groups run in parallel, second Return() evicts.
        BlockCachePreWarmer preWarmer = new(
            trackingPolicy,
            maxPoolSize: 1,
            concurrency: 2,
            nodeStorageCache,
            preBlockCaches,
            LimboLogs.Instance);

        BlockHeader parentHeader = Build.A.BlockHeader
            .WithNumber(0)
            .WithStateRoot(_genesisStateRoot)
            .WithGasLimit(30_000_000)
            .TestObject;

        // Two senders → two sender groups → two parallel workers → eviction with pool capacity 1.
        Transaction[] txs =
        [
            Build.A.Transaction.WithNonce(0).WithTo(TestItem.AddressC).WithValue(1.Wei).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
            Build.A.Transaction.WithNonce(0).WithTo(TestItem.AddressC).WithValue(1.Wei).SignedAndResolved(TestItem.PrivateKeyB).TestObject,
        ];

        Block block = Build.A.Block
            .WithParent(parentHeader)
            .WithTransactions(txs)
            .WithGasLimit(30_000_000)
            .TestObject;

        await preWarmer.PreWarmCaches(block, parentHeader, Osaka.Instance);

        // Every env that was created but could not be retained must have been disposed.
        // With pool capacity 1 and concurrency 2, at least one eviction must have occurred.
        created.Count.Should().BeGreaterThanOrEqualTo(2,
            "two distinct senders must have exercised two concurrent workers, " +
            "otherwise the eviction scenario was never reached");
        int evictedCount = created.Count - 1; // at most 1 retained in pool
        disposed.Count.Should().BeGreaterThanOrEqualTo(evictedCount,
            "all envs evicted from the pool must have Dispose() called immediately " +
            "so their child LifetimeScopes and WorldStates are released, " +
            "not leaked until process shutdown");
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
