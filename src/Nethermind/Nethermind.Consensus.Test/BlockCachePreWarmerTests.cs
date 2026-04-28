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
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
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

        // Seed genesis state for all sender accounts and storage, then capture the root.
        IWorldState worldState = _processingScope.Resolve<IWorldState>();
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            worldState.CreateAccount(TestItem.AddressA, 1_000_000.Ether);
            worldState.CreateAccount(TestItem.AddressB, 1_000_000.Ether);
            // Seed storage for BAL-based prewarming tests
            worldState.Set(new StorageCell(TestItem.AddressA, 1), new byte[] { 0x42 });
            worldState.Set(new StorageCell(TestItem.AddressA, 2), new byte[] { 0x43 });
            worldState.Set(new StorageCell(TestItem.AddressB, 10), new byte[] { 0x99 });
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

    /// <summary>
    /// Verifies that BAL-based prewarming populates the account state cache for all
    /// addresses listed in the BlockAccessList.
    /// </summary>
    [Test]
    public async Task PreWarmCaches_WithBal_PopulatesStateCacheForBalAddresses()
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        (BlockCachePreWarmer preWarmer, _, _) = CreatePreWarmer(maxPoolSize: 10);

        BlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).TestObject)
            .TestObject;

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithBlockAccessList(bal)
            .TestObject;

        await preWarmer.PreWarmCaches(block, BuildParentHeader(), Amsterdam.Instance);

        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out _).Should().BeTrue(
            "AddressA is in the BAL and should be pre-warmed");
        preBlockCaches.StateCache.TryGetValue(TestItem.AddressB, out _).Should().BeTrue(
            "AddressB is in the BAL and should be pre-warmed");
    }

    /// <summary>
    /// Verifies that BAL-based prewarming populates the storage cache for storage slots
    /// listed as both changed and read-only in the BAL.
    /// </summary>
    [Test]
    public async Task PreWarmCaches_WithBal_PopulatesStorageCacheForBalSlots()
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        (BlockCachePreWarmer preWarmer, _, _) = CreatePreWarmer(maxPoolSize: 10);

        BlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithStorageChanges(1, new StorageChange(0, 0xFF)) // slot 1 written
                    .WithStorageReads(2) // slot 2 read-only
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressB)
                    .WithStorageReads(10) // slot 10 read-only
                    .TestObject)
            .TestObject;

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithBlockAccessList(bal)
            .TestObject;

        await preWarmer.PreWarmCaches(block, BuildParentHeader(), Amsterdam.Instance);

        // Changed slot should be warmed
        preBlockCaches.StorageCache.TryGetValue(new StorageCell(TestItem.AddressA, 1), out _).Should().BeTrue(
            "slot 1 (changed) should be pre-warmed via BAL");
        // Read-only slot should be warmed
        preBlockCaches.StorageCache.TryGetValue(new StorageCell(TestItem.AddressA, 2), out _).Should().BeTrue(
            "slot 2 (read-only) should be pre-warmed via BAL");
        // Storage from a different account
        preBlockCaches.StorageCache.TryGetValue(new StorageCell(TestItem.AddressB, 10), out _).Should().BeTrue(
            "slot 10 on AddressB should be pre-warmed via BAL");
    }

    /// <summary>
    /// Verifies that when ParallelExecutionBatchRead is disabled, the BAL path is not used
    /// even when a BAL is present and EIP-7928 is enabled.
    /// </summary>
    [Test]
    public async Task PreWarmCaches_WithBalButFlagDisabled_SkipsBalPath()
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        (BlockCachePreWarmer preWarmer, _, _) = CreatePreWarmer(maxPoolSize: 10, parallelExecutionBatchRead: false);

        BlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressC).TestObject)
            .TestObject;

        // Block has < 3 txs — with BAL disabled, prewarming is skipped entirely
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithBlockAccessList(bal)
            .TestObject;

        await preWarmer.PreWarmCaches(block, BuildParentHeader(), Amsterdam.Instance);

        preBlockCaches.StateCache.TryGetValue(TestItem.AddressC, out _).Should().BeFalse(
            "BAL path should be skipped when ParallelExecutionBatchRead is disabled");
    }

    /// <summary>
    /// Verifies that the BAL path is not triggered for pre-Amsterdam specs even when a
    /// BAL is attached to the block.
    /// </summary>
    [Test]
    public async Task PreWarmCaches_WithBalButPreAmsterdam_UsesSpeculativePath()
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        (BlockCachePreWarmer preWarmer, _, _) = CreatePreWarmer(maxPoolSize: 10);

        BlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject)
            .TestObject;

        // Block has enough txs to trigger speculative prewarming
        Block block = Build.A.Block
            .WithTransactions(BuildTwoSenderBlock().Transactions)
            .WithGasLimit(30_000_000)
            .WithBlockAccessList(bal)
            .TestObject;

        // Use Osaka which does NOT have EIP-7928 — BAL path should not trigger
        await preWarmer.PreWarmCaches(block, BuildParentHeader(), Osaka.Instance);

        // AddressA should still be warmed via speculative tx execution (not BAL path)
        // since it's a sender in the transactions
        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out _).Should().BeTrue(
            "AddressA should be warmed via speculative execution even without BAL path");
    }

    /// <summary>
    /// Verifies the prewarmer gate logic: ParallelExecution ON disables prewarming unless
    /// ParallelExecutionBatchRead is ON and BALs are available.
    /// </summary>
    [TestCase(true, true, true, true, TestName = "ParallelExec ON, BALs ON, BatchRead ON => BAL warming")]
    [TestCase(true, true, false, false, TestName = "ParallelExec ON, BALs ON, BatchRead OFF => skipped")]
    [TestCase(true, false, true, false, TestName = "ParallelExec ON, BALs OFF, BatchRead ON => skipped")]
    [TestCase(true, false, false, false, TestName = "ParallelExec ON, BALs OFF, BatchRead OFF => skipped")]
    [TestCase(false, true, true, true, TestName = "ParallelExec OFF, BALs ON, BatchRead ON => BAL warming")]
    [TestCase(false, false, false, true, TestName = "ParallelExec OFF, BALs OFF, BatchRead OFF => speculative")]
    public async Task PreWarmCaches_GateLogic(bool parallelExecution, bool hasBal, bool batchRead, bool expectWarmed)
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        BlockCachePreWarmer preWarmer = CreatePreWarmerFromConfig(parallelExecution, batchRead);

        BlockAccessList? bal = hasBal
            ? Build.A.BlockAccessList
                .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject)
                .TestObject
            : null;

        // Use Amsterdam (EIP-7928) when testing BALs, Osaka otherwise
        IReleaseSpec spec = hasBal ? Amsterdam.Instance : Osaka.Instance;

        Block block = Build.A.Block
            .WithTransactions(BuildTwoSenderBlock().Transactions)
            .WithGasLimit(30_000_000)
            .WithBlockAccessList(bal)
            .TestObject;

        await preWarmer.PreWarmCaches(block, BuildParentHeader(), spec);

        preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out _).Should().Be(expectWarmed,
            $"ParallelExec={parallelExecution}, BALs={hasBal}, BatchRead={batchRead} => warmed={expectWarmed}");
    }

    private BlockCachePreWarmer CreatePreWarmerFromConfig(bool parallelExecution, bool parallelExecutionBatchRead)
    {
        PrewarmerEnvFactory envFactory = _processingScope.Resolve<PrewarmerEnvFactory>();
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        NodeStorageCache nodeStorageCache = _processingScope.Resolve<NodeStorageCache>();

        BlocksConfig config = new()
        {
            PreWarmStateOnBlockProcessing = true,
            PreWarmStateConcurrency = 2,
            ParallelExecution = parallelExecution,
            ParallelExecutionBatchRead = parallelExecutionBatchRead
        };

        return new BlockCachePreWarmer(envFactory, config, nodeStorageCache, preBlockCaches, LimboLogs.Instance);
    }

    private (BlockCachePreWarmer, ConcurrentBag<IReadOnlyTxProcessorSource> created, ConcurrentBag<IReadOnlyTxProcessorSource> disposed) CreatePreWarmer(int maxPoolSize, bool parallelExecutionBatchRead = true)
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
            parallelExecutionBatchRead: parallelExecutionBatchRead,
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
