// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Container;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;
using PrewarmMetrics = Nethermind.Consensus.Processing.Prewarming.Metrics;

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
    /// Verifies that an env returned to a full pool is disposed immediately.
    /// </summary>
    [Test]
    public void EnvPool_ReturnedBeyondCapacity_IsDisposedImmediately()
    {
        PrewarmerEnvFactory envFactory = _processingScope.Resolve<PrewarmerEnvFactory>();
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();

        ConcurrentBag<IReadOnlyTxProcessorSource> created = [];
        ConcurrentBag<IReadOnlyTxProcessorSource> disposed = [];
        DisposalTrackingPolicy trackingPolicy = new(envFactory, preBlockCaches, created, disposed);

        ObjectPool<IReadOnlyTxProcessorSource> envPool = new DefaultObjectPoolProvider { MaximumRetained = 1 }.Create(trackingPolicy);

        IReadOnlyTxProcessorSource first = envPool.Get();
        IReadOnlyTxProcessorSource second = envPool.Get();
        Assert.That(created.Count, Is.EqualTo(2), "precondition: an empty pool must create one env per overlapping rental");

        envPool.Return(first);
        envPool.Return(second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(disposed.Count, Is.EqualTo(1), "the env returned beyond pool capacity must be disposed on the spot");
            Assert.That(disposed, Does.Contain(second), "the first return fills the capacity-1 pool, so the second is the evicted one");
        }

        (envPool as IDisposable)?.Dispose();
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

        await RunPreWarmCaches(preWarmer, BuildTwoSenderBlock(), BuildParentHeader(), Osaka.Instance);

        Assert.That(disposed.Count, Is.EqualTo(0), "no eviction should have occurred with a large pool");
        Assert.That(created.Count, Is.GreaterThanOrEqualTo(1), "at least one env must have been created");

        preWarmer.Dispose();

        Assert.That(disposed.Count, Is.EqualTo(created.Count), "all retained envs must be disposed when the prewarmer is disposed");
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

        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject,
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).TestObject)
            .TestObject;

        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithBlockAccessList(bal)
            .TestObject;

        await RunPreWarmCaches(preWarmer, block, BuildParentHeader(), Amsterdam.Instance);

        Assert.That(preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out _), Is.True, "AddressA is in the BAL and should be pre-warmed");
        Assert.That(preBlockCaches.StateCache.TryGetValue(TestItem.AddressB, out _), Is.True, "AddressB is in the BAL and should be pre-warmed");
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

        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
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

        await RunPreWarmCaches(preWarmer, block, BuildParentHeader(), Amsterdam.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preBlockCaches.StorageCache.TryGetValue(new StorageCell(TestItem.AddressA, 1), out _), Is.True, "slot 1 (changed) should be pre-warmed via BAL");
            Assert.That(preBlockCaches.StorageCache.TryGetValue(new StorageCell(TestItem.AddressA, 2), out _), Is.True, "slot 2 (read-only) should be pre-warmed via BAL");
            Assert.That(preBlockCaches.StorageCache.TryGetValue(new StorageCell(TestItem.AddressB, 10), out _), Is.True, "slot 10 on AddressB should be pre-warmed via BAL");
        }
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

        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges.WithAddress(TestItem.AddressC).TestObject)
            .TestObject;

        // Block has < 3 txs — with BAL disabled, prewarming is skipped entirely
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithBlockAccessList(bal)
            .TestObject;

        await RunPreWarmCaches(preWarmer, block, BuildParentHeader(), Amsterdam.Instance);

        Assert.That(preBlockCaches.StateCache.TryGetValue(TestItem.AddressC, out _), Is.False, "BAL path should be skipped when ParallelExecutionBatchRead is disabled");
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

        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
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
        await RunPreWarmCaches(preWarmer, block, BuildParentHeader(), Osaka.Instance);

        // AddressA should still be warmed via speculative tx execution (not BAL path)
        // since it's a sender in the transactions
        Assert.That(preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out _), Is.True, "AddressA should be warmed via speculative execution even without BAL path");
    }

    /// <summary>Prewarming warms a transaction's declared EIP-2930 access-list slots for the main thread.</summary>
    [Test]
    public async Task PreWarmCaches_WarmsAccessList_AndHonorsCancellation()
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        (BlockCachePreWarmer preWarmer, _, _) = CreatePreWarmer(maxPoolSize: 10);

        StorageCell declaredSlot = new(TestItem.AddressB, 10); // seeded in genesis
        AccessList accessList = new AccessList.Builder()
            .AddAddress(TestItem.AddressB)
            .AddStorage(declaredSlot.Index)
            .Build();

        Transaction[] txs =
        [
            Build.A.Transaction.WithType(TxType.AccessList).WithNonce(0).WithTo(TestItem.AddressC).WithValue(1.Wei)
                .WithAccessList(accessList).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
            Build.A.Transaction.WithNonce(0).WithTo(TestItem.AddressC).WithValue(1.Wei)
                .SignedAndResolved(TestItem.PrivateKeyB).TestObject,
        ];
        Block block = Build.A.Block.WithTransactions(txs).WithGasLimit(30_000_000).TestObject;

        await RunPreWarmCaches(preWarmer, block, BuildParentHeader(), Osaka.Instance);

        Assert.That(preBlockCaches.StorageCache.TryGetValue(in declaredSlot, out _), Is.True,
            "declared access-list slots are warmed for the main thread");
    }

    /// <summary>A cancelled token stops the access-list warm (storage-key dimension): live warms the tail slot, cancelled does not.</summary>
    [Test]
    public void WarmUp_AccessList_StopsOnCancellation()
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        IWorldState worldState = _processingScope.Resolve<IWorldState>();

        AccessList.Builder builder = new AccessList.Builder().AddAddress(TestItem.AddressB);
        for (int i = 0; i < 256; i++) builder.AddStorage((UInt256)(1000 + i));
        AccessList accessList = builder.Build();
        StorageCell tailSlot = new(TestItem.AddressB, 1255);

        using (worldState.BeginScope(BuildParentHeader()))
        {
            worldState.WarmUp(accessList, CancellationToken.None);
            bool warmedLive = preBlockCaches.StorageCache.TryGetValue(in tailSlot, out _);

            preBlockCaches.StorageCache.Clear();
            worldState.WarmUp(accessList, new CancellationToken(canceled: true));
            bool warmedCancelled = preBlockCaches.StorageCache.TryGetValue(in tailSlot, out _);

            Assert.That(warmedLive, Is.True, "a live token warms the whole access list");
            Assert.That(warmedCancelled, Is.False, "a cancelled token stops the warm before the tail");
        }
    }

    /// <summary>A cancelled token stops the per-address access-list warm (many address-only entries): live warms the tail address, cancelled does not.</summary>
    [Test]
    public void WarmUp_AccessList_StopsOnCancellation_ManyAddresses()
    {
        static Address AddrFromIndex(int i)
        {
            byte[] b = new byte[20];
            b[16] = (byte)(i >> 24); b[17] = (byte)(i >> 16); b[18] = (byte)(i >> 8); b[19] = (byte)i;
            return new Address(b);
        }

        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        IWorldState worldState = _processingScope.Resolve<IWorldState>();

        AccessList.Builder builder = new();
        for (int i = 0; i < 256; i++) builder.AddAddress(AddrFromIndex(i)); // address-only entries, no storage keys
        AccessList accessList = builder.Build();
        Address tail = AddrFromIndex(255);

        using (worldState.BeginScope(BuildParentHeader()))
        {
            worldState.WarmUp(accessList, CancellationToken.None);
            bool warmedLive = preBlockCaches.StateCache.TryGetValue(tail, out _);

            preBlockCaches.StateCache.Clear();
            worldState.WarmUp(accessList, new CancellationToken(canceled: true));
            bool warmedCancelled = preBlockCaches.StateCache.TryGetValue(tail, out _);

            Assert.That(warmedLive, Is.True, "a live token warms every declared address");
            Assert.That(warmedCancelled, Is.False, "a cancelled token stops the per-address warm before the tail");
        }
    }

    /// <summary>
    /// Verifies that the prewarmer does not inherit the <see cref="ProcessingThread.IsBlockProcessingThread"/>
    /// flag from the caller, so speculative EVM work is attributed to the _other* metric counters.
    /// </summary>
    [Test]
    public async Task PreWarmCaches_DoesNotFlowIsBlockProcessingThread_IntoTask()
    {
        bool observedFlag = true;
        using ManualResetEventSlim observed = new(initialState: false);

        PrewarmerEnvFactory envFactory = _processingScope.Resolve<PrewarmerEnvFactory>();
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        NodeStorageCache nodeStorageCache = _processingScope.Resolve<NodeStorageCache>();

        FlagCapturingPolicy flagPolicy = new(envFactory, preBlockCaches, observed, v => observedFlag = v);
        using BlockCachePreWarmer flagWarmer = new(
            flagPolicy,
            maxPoolSize: 10,
            concurrency: 2,
            parallelExecutionBatchRead: true,
            nodeStorageCache,
            preBlockCaches,
            LimboLogs.Instance);

        Task prewarmTask = Task.CompletedTask;
        ProcessingThread.IsBlockProcessingThread = true;
        try
        {
            prewarmTask = flagWarmer.PreWarmCaches(BuildTwoSenderBlock(), BuildParentHeader(), Osaka.Instance);
            Assert.That(
                ProcessingThread.IsBlockProcessingThread,
                Is.True,
                "scheduling prewarming must not disturb the caller's thread-local flag");
        }
        finally
        {
            ProcessingThread.IsBlockProcessingThread = false;
        }

        await prewarmTask;
        Assert.That(observed.Wait(TimeSpan.FromSeconds(5)), Is.True, "the flag-capturing policy must have been invoked");
        Assert.That(observedFlag, Is.False, "IsBlockProcessingThread must be false inside the prewarmer task");
    }

    /// <summary>
    /// Verifies the prewarmer gate logic: ParallelExecution ON skips speculative prewarming
    /// only when BAL is active for the spec (so parallel execution can actually run); when
    /// BAL is not active, speculative prewarming runs regardless of ParallelExecution.
    /// </summary>
    [TestCase(true, true, true, true, TestName = "ParallelExec ON, BALs ON, BatchRead ON => BAL warming")]
    [TestCase(true, true, false, false, TestName = "ParallelExec ON, BALs ON, BatchRead OFF => skipped")]
    [TestCase(true, false, true, true, TestName = "ParallelExec ON, BALs OFF, BatchRead ON => speculative")]
    [TestCase(true, false, false, true, TestName = "ParallelExec ON, BALs OFF, BatchRead OFF => speculative")]
    [TestCase(false, true, true, true, TestName = "ParallelExec OFF, BALs ON, BatchRead ON => BAL warming")]
    [TestCase(false, false, false, true, TestName = "ParallelExec OFF, BALs OFF, BatchRead OFF => speculative")]
    public async Task PreWarmCaches_GateLogic(bool parallelExecution, bool hasBal, bool batchRead, bool expectWarmed)
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        BlockCachePreWarmer preWarmer = CreatePreWarmerFromConfig(parallelExecution, batchRead);

        ReadOnlyBlockAccessList? bal = hasBal
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

        await RunPreWarmCaches(preWarmer, block, BuildParentHeader(), spec);

        Assert.That(preBlockCaches.StateCache.TryGetValue(TestItem.AddressA, out _), Is.EqualTo(expectWarmed), $"ParallelExec={parallelExecution}, BALs={hasBal}, BatchRead={batchRead} => warmed={expectWarmed}");
    }

    [Test]
    public async Task ParentReaderEnvPolicy_SharesBalWarmupCachesAndPopulatesMisses()
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        PrewarmerEnvFactory envFactory = _processingScope.Resolve<PrewarmerEnvFactory>();
        using BlockCachePreWarmer preWarmer = CreatePreWarmerFromConfig(parallelExecution: true, parallelExecutionBatchRead: true);

        StorageCell warmedCell = new(TestItem.AddressA, 1);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithStorageReads(1)
                    .TestObject)
            .TestObject;
        Block block = Build.A.Block
            .WithGasLimit(30_000_000)
            .WithBlockAccessList(bal)
            .TestObject;

        await RunPreWarmCaches(preWarmer, block, BuildParentHeader(), Amsterdam.Instance);

        AddressAsKey warmedAddress = TestItem.AddressA;
        Assert.That(preBlockCaches.StateCache.TryGetValue(in warmedAddress, out _), Is.True);
        Assert.That(preBlockCaches.StorageCache.TryGetValue(in warmedCell, out _), Is.True);

        preBlockCaches.StateCache.Set(in warmedAddress, new Account(777UL));
        preBlockCaches.StorageCache.Set(in warmedCell, [0x24]);

        AddressAsKey missedAddress = TestItem.AddressB;
        StorageCell missedCell = new(TestItem.AddressB, 10);
        Assert.That(preBlockCaches.StateCache.TryGetValue(in missedAddress, out _), Is.False);
        Assert.That(preBlockCaches.StorageCache.TryGetValue(in missedCell, out _), Is.False);

        BlockCachePreWarmer.ReadOnlyTxProcessingEnvPooledObjectPolicy validationPolicy = new(envFactory, preBlockCaches);
        using IReadOnlyTxProcessorSource source = validationPolicy.Create();
        using IReadOnlyTxProcessingScope scope = source.Build(BuildParentHeader());

        Assert.That(scope.WorldState.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)777));
        Assert.That(new UInt256(scope.WorldState.Get(warmedCell), isBigEndian: true), Is.EqualTo((UInt256)0x24));

        Assert.That(scope.WorldState.GetBalance(TestItem.AddressB), Is.EqualTo(1_000_000.Ether));
        Assert.That(new UInt256(scope.WorldState.Get(missedCell), isBigEndian: true), Is.EqualTo((UInt256)0x99));

        Assert.That(preBlockCaches.StateCache.TryGetValue(in missedAddress, out Account? populatedAccount), Is.True,
            "parallel validation parent readers must populate cache misses");
        Assert.That(populatedAccount!.Balance, Is.EqualTo(1_000_000.Ether));
        Assert.That(preBlockCaches.StorageCache.TryGetValue(in missedCell, out byte[]? populatedStorage), Is.True);
        Assert.That(new UInt256(populatedStorage, isBigEndian: true), Is.EqualTo((UInt256)0x99));
    }

    [TestCase(false, true, false, TestName = "PreWarmCaches_NoSpeculativePass_Clears")]
    [TestCase(true, true, true, TestName = "PreWarmCaches_SameParent_HandsOff")]
    [TestCase(true, false, false, TestName = "PreWarmCaches_DifferentParent_Clears")]
    public async Task PreWarmCaches_HandoffSentinelSurvival(bool speculative, bool sameParent, bool expectSurvives)
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        (BlockCachePreWarmer preWarmer, _, _) = CreatePreWarmer(maxPoolSize: 10);
        BlockHeader head = BuildParentHeader();

        if (speculative) await RunSpeculativePreWarm(preWarmer, head, Osaka.Instance);

        AddressAsKey sentinel = TestItem.AddressD;
        preBlockCaches.StateCache.Set(in sentinel, new Account(123));

        Nethermind.Core.Crypto.Hash256 parentHash = sameParent ? head.Hash! : TestItem.KeccakA;
        Block next = Build.A.Block.WithTransactions(BuildTwoSenderBlock().Transactions)
            .WithGasLimit(30_000_000).WithParentHash(parentHash).TestObject;
        await RunPreWarmCaches(preWarmer, next, head, Osaka.Instance);

        Assert.That(preBlockCaches.StateCache.TryGetValue(in sentinel, out _), Is.EqualTo(expectSurvives),
            "sentinel survives only on a matching-parent handoff");
    }

    [Test]
    public async Task PreWarmCaches_HandoffMarker_IsConsumedOnce()
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        (BlockCachePreWarmer preWarmer, _, _) = CreatePreWarmer(maxPoolSize: 10);

        BlockHeader head = BuildParentHeader();
        await RunSpeculativePreWarm(preWarmer, head, Osaka.Instance);

        await RunPreWarmCaches(preWarmer, BuildChildBlock(head), head, Osaka.Instance);

        AddressAsKey sentinel = TestItem.AddressD;
        preBlockCaches.StateCache.Set(in sentinel, new Account(123));

        await RunPreWarmCaches(preWarmer, BuildChildBlock(head), head, Osaka.Instance);

        Assert.That(preBlockCaches.StateCache.TryGetValue(in sentinel, out _), Is.False,
            "the handoff marker must only be honored once");
    }

    [Test]
    public async Task StartSpeculativePreWarm_CachesCommittedBaseState_NotSpeculativeWrites()
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        (BlockCachePreWarmer preWarmer, _, _) = CreatePreWarmer(maxPoolSize: 10);

        await RunSpeculativePreWarm(preWarmer, BuildParentHeader(), Osaka.Instance);

        AddressAsKey senderA = TestItem.AddressA;
        Assert.That(preBlockCaches.StateCache.TryGetValue(in senderA, out Account? cachedA), Is.True,
            "sender A must be warmed by speculative execution");
        Assert.That(cachedA!.Balance, Is.EqualTo(1_000_000.Ether),
            "the cache must hold A's committed balance, not the post-execution (value + gas deducted) balance");
    }

    [Test]
    public async Task PreWarmCaches_AfterHandoff_SkipsSpeculativelyWarmedSenders()
    {
        PrewarmerEnvFactory envFactory = _processingScope.Resolve<PrewarmerEnvFactory>();
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        NodeStorageCache nodeStorageCache = _processingScope.Resolve<NodeStorageCache>();

        int warmups = 0;
        using ManualResetEventSlim openGate = new(initialState: true);
        WarmupCountingPolicy policy = new(envFactory, preBlockCaches, openGate, () => Interlocked.Increment(ref warmups));
        using BlockCachePreWarmer preWarmer = new(policy, maxPoolSize: 10, concurrency: 2,
            parallelExecutionBatchRead: true, nodeStorageCache, preBlockCaches, LimboLogs.Instance);

        BlockHeader head = BuildParentHeader();
        await RunSpeculativePreWarm(preWarmer, head, Osaka.Instance);
        int speculativeWarmups = Volatile.Read(ref warmups);
        Volatile.Write(ref warmups, 0);

        await RunPreWarmCaches(preWarmer, BuildChildBlock(head), head, Osaka.Instance);

        Assert.That(speculativeWarmups, Is.GreaterThan(0), "precondition: the speculative pass warmed the transactions");
        Assert.That(Volatile.Read(ref warmups), Is.EqualTo(0), "the reactive pass must skip senders already fully warmed speculatively");
    }

    [Test]
    public void GroupTransactionsBySender_SameSenderStaysGroupedInOrder()
    {
        Block block = Build.A.Block.WithTransactions(
            GroupingTx(TestItem.PrivateKeyA, nonce: 0, gasLimit: 100_000),
            GroupingTx(TestItem.PrivateKeyB, nonce: 0, gasLimit: 100_000),
            GroupingTx(TestItem.PrivateKeyA, nonce: 1, gasLimit: 100_000)).TestObject;

        ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups = BlockCachePreWarmer.GroupTransactionsBySender(block, maxWorkers: 4);
        try
        {
            Assert.That(groups.Count, Is.EqualTo(2));
            ArrayPoolList<(int Index, Transaction Tx)> senderA = FindGroup(groups, TestItem.AddressA);
            Assert.That(senderA.Count, Is.EqualTo(2));
            Assert.That(senderA[0].Index, Is.EqualTo(0));
            Assert.That(senderA[1].Index, Is.EqualTo(2));
        }
        finally
        {
            DisposeGroups(groups);
        }
    }

    // The split threshold is strictly greater-than 4,000,000 aggregate declared gas.
    [TestCase(1_900_000u, 1)]
    [TestCase(2_000_000u, 1)]
    [TestCase(2_000_001u, 2)]
    [TestCase(3_000_000u, 2)]
    public void GroupTransactionsBySender_SplitsOnlyAboveAggregateThreshold(uint gasPerTx, int expectedJobs)
    {
        Block block = Build.A.Block.WithTransactions(
            GroupingTx(TestItem.PrivateKeyA, nonce: 0, gasLimit: gasPerTx),
            GroupingTx(TestItem.PrivateKeyA, nonce: 1, gasLimit: gasPerTx)).TestObject;

        ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups = BlockCachePreWarmer.GroupTransactionsBySender(block, maxWorkers: 4);
        try
        {
            Assert.That(groups.Count, Is.EqualTo(expectedJobs));
            if (expectedJobs > 1)
            {
                foreach (ArrayPoolList<(int Index, Transaction Tx)> group in groups.AsSpan())
                {
                    Assert.That(group.Count, Is.EqualTo(1), "a split group must warm per-tx");
                }
            }
        }
        finally
        {
            DisposeGroups(groups);
        }
    }

    [Test]
    public void GroupTransactionsBySender_DoesNotSplitSingleHeavyTransaction()
    {
        Block block = Build.A.Block.WithTransactions(
            GroupingTx(TestItem.PrivateKeyA, nonce: 0, gasLimit: 5_000_000)).TestObject;

        ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups = BlockCachePreWarmer.GroupTransactionsBySender(block, maxWorkers: 4);
        try
        {
            Assert.That(groups.Count, Is.EqualTo(1), "there is nothing to parallelize within a single transaction");
        }
        finally
        {
            DisposeGroups(groups);
        }
    }

    // Below two workers a split cannot add parallelism; it only discards same-sender state
    // propagation. Negative means unlimited, matching ParallelOptions.MaxDegreeOfParallelism.
    [TestCase(1, 1)]
    [TestCase(2, 2)]
    [TestCase(-1, 2)]
    public void GroupTransactionsBySender_SplitsOnlyWithParallelWorkers(int maxWorkers, int expectedJobs)
    {
        Block block = Build.A.Block.WithTransactions(
            GroupingTx(TestItem.PrivateKeyA, nonce: 0, gasLimit: 3_000_000),
            GroupingTx(TestItem.PrivateKeyA, nonce: 1, gasLimit: 3_000_000)).TestObject;

        ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups = BlockCachePreWarmer.GroupTransactionsBySender(block, maxWorkers);
        try
        {
            Assert.That(groups.Count, Is.EqualTo(expectedJobs));
        }
        finally
        {
            DisposeGroups(groups);
        }
    }

    [Test]
    public void GroupTransactionsBySender_SaturatesAggregateGasInsteadOfWrapping()
    {
        // Without saturation these two declared limits sum to exactly 4,000,000 (mod 2^64),
        // which is not above the threshold, and the wrap would suppress the split.
        Block block = Build.A.Block.WithTransactions(
            GroupingTx(TestItem.PrivateKeyA, nonce: 0, gasLimit: ulong.MaxValue),
            GroupingTx(TestItem.PrivateKeyA, nonce: 1, gasLimit: 4_000_001)).TestObject;

        ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups = BlockCachePreWarmer.GroupTransactionsBySender(block, maxWorkers: 4);
        try
        {
            Assert.That(groups.Count, Is.EqualTo(2), "an extreme declared gas limit must saturate, not wrap, the aggregate");
        }
        finally
        {
            DisposeGroups(groups);
        }
    }

    [Test]
    public void GroupTransactionsBySender_HoistsHeavyGroupsAndKeepsRestInBlockOrder()
    {
        Block block = Build.A.Block.WithTransactions(
            GroupingTx(TestItem.PrivateKeyB, nonce: 0, gasLimit: 100_000),
            GroupingTx(TestItem.PrivateKeyB, nonce: 1, gasLimit: 100_000),
            GroupingTx(TestItem.PrivateKeyC, nonce: 0, gasLimit: 1_000_000),
            GroupingTx(TestItem.PrivateKeyA, nonce: 0, gasLimit: 5_000_000)).TestObject;

        ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups = BlockCachePreWarmer.GroupTransactionsBySender(block, maxWorkers: 4);
        try
        {
            Assert.That(groups.Count, Is.EqualTo(3));
            Assert.That(groups[0][0].Tx.SenderAddress, Is.EqualTo(TestItem.AddressA), "heavy group is hoisted to the front");
            Assert.That(groups[1][0].Tx.SenderAddress, Is.EqualTo(TestItem.AddressB), "light groups keep block order");
            Assert.That(groups[2][0].Tx.SenderAddress, Is.EqualTo(TestItem.AddressC));
        }
        finally
        {
            DisposeGroups(groups);
        }
    }

    // Pins the current policy: split children below the individual hoist threshold are ordinary
    // jobs and stay in block order rather than inheriting the parent group's weight.
    [Test]
    public void GroupTransactionsBySender_SplitChildrenBelowHoistThresholdKeepBlockOrder()
    {
        Block block = Build.A.Block.WithTransactions(
            GroupingTx(TestItem.PrivateKeyB, nonce: 0, gasLimit: 100_000),
            GroupingTx(TestItem.PrivateKeyA, nonce: 0, gasLimit: 3_000_000),
            GroupingTx(TestItem.PrivateKeyA, nonce: 1, gasLimit: 3_000_000)).TestObject;

        ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups = BlockCachePreWarmer.GroupTransactionsBySender(block, maxWorkers: 4);
        try
        {
            Assert.That(groups.Count, Is.EqualTo(3));
            Assert.That(groups[0][0].Tx.SenderAddress, Is.EqualTo(TestItem.AddressB), "the light sender at block index 0 warms first");
            Assert.That(groups[1][0].Index, Is.EqualTo(1));
            Assert.That(groups[2][0].Index, Is.EqualTo(2));
        }
        finally
        {
            DisposeGroups(groups);
        }
    }

    // Asserts deltas on process-wide static counters; keep serialized in case assembly-level
    // parallelism is ever enabled.
    [Test]
    [NonParallelizable]
    public void GroupTransactionsBySender_CountsSendersOncePerOriginalGroupAndPrunesWarmedJobs()
    {
        Transaction[] heavyChain =
        [
            GroupingTx(TestItem.PrivateKeyA, nonce: 0, gasLimit: 1_000_000),
            GroupingTx(TestItem.PrivateKeyA, nonce: 1, gasLimit: 1_000_000),
            GroupingTx(TestItem.PrivateKeyA, nonce: 2, gasLimit: 1_000_000),
            GroupingTx(TestItem.PrivateKeyA, nonce: 3, gasLimit: 1_000_000),
            GroupingTx(TestItem.PrivateKeyA, nonce: 4, gasLimit: 1_000_000),
        ];
        Transaction warmedLight = GroupingTx(TestItem.PrivateKeyB, nonce: 0, gasLimit: 100_000);
        Block block = Build.A.Block.WithTransactions([.. heavyChain, warmedLight]).TestObject;

        HashSet<Nethermind.Core.Crypto.Hash256> warmed =
            [heavyChain[0].Hash!, heavyChain[1].Hash!, heavyChain[2].Hash!, warmedLight.Hash!];

        long skippedBefore = PrewarmMetrics.MempoolPrewarmSendersSkipped;
        long warmedBefore = PrewarmMetrics.MempoolPrewarmSendersWarmed;

        ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups =
            BlockCachePreWarmer.GroupTransactionsBySender(block, maxWorkers: 4, warmed);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(PrewarmMetrics.MempoolPrewarmSendersSkipped - skippedBefore, Is.EqualTo(1),
                    "the fully warmed sender counts once");
                Assert.That(PrewarmMetrics.MempoolPrewarmSendersWarmed - warmedBefore, Is.EqualTo(1),
                    "the partially warmed sender counts once despite splitting into several jobs");
                Assert.That(groups.Count, Is.EqualTo(2), "already-warmed split children are pruned");
            }
            foreach (ArrayPoolList<(int Index, Transaction Tx)> group in groups.AsSpan())
            {
                Assert.That(group[0].Index, Is.InRange(3, 4), "only the unwarmed tail of the chain remains");
            }
        }
        finally
        {
            DisposeGroups(groups);
        }
    }

    private static Transaction GroupingTx(PrivateKey sender, uint nonce, ulong gasLimit) =>
        Build.A.Transaction.WithNonce(nonce).WithGasLimit(gasLimit).WithTo(TestItem.AddressD).SignedAndResolved(sender).TestObject;

    private static ArrayPoolList<(int Index, Transaction Tx)> FindGroup(
        ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups, Address sender)
    {
        foreach (ArrayPoolList<(int Index, Transaction Tx)> group in groups.AsSpan())
        {
            if (group[0].Tx.SenderAddress == sender) return group;
        }

        throw new InvalidOperationException($"No group for {sender}");
    }

    private static void DisposeGroups(ArrayPoolList<ArrayPoolList<(int Index, Transaction Tx)>> groups)
    {
        foreach (ArrayPoolList<(int Index, Transaction Tx)> group in groups.AsSpan())
        {
            group.Dispose();
        }
        groups.Dispose();
    }

    private Block BuildChildBlock(BlockHeader head) =>
        Build.A.Block.WithTransactions(BuildTwoSenderBlock().Transactions)
            .WithGasLimit(30_000_000).WithParentHash(head.Hash!).TestObject;

    private Task RunSpeculativePreWarm(BlockCachePreWarmer preWarmer, BlockHeader head, IReleaseSpec spec)
    {
        Block delta = BuildTwoSenderBlock();
        int calls = 0;
        Block? Next(CancellationToken _) => Interlocked.Increment(ref calls) == 1 ? delta : null;

        using CancellationTokenSource cts = new();
        Task task = preWarmer.StartSpeculativePreWarm(head, spec, generation: 1, Next, idlePassDelayMs: 5, cts.Token);
        SpinWait.SpinUntil(() => preWarmer.SpeculativeMarkerPublished, TimeSpan.FromSeconds(5));
        cts.Cancel();
        task.GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    private BlockCachePreWarmer CreatePreWarmerFromConfig(bool parallelExecution, bool parallelExecutionBatchRead)
    {
        PrewarmerEnvFactory envFactory = _processingScope.Resolve<PrewarmerEnvFactory>();
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        NodeStorageCache nodeStorageCache = _processingScope.Resolve<NodeStorageCache>();

        BlocksConfig config = new()
        {
            PreWarming = PreWarmMode.Block,
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

    // Sync on purpose — TrieStore's Lock-based BeginScope dispose must run on the same thread.
    private Task RunPreWarmCaches(BlockCachePreWarmer preWarmer, Block block, BlockHeader parent, IReleaseSpec spec)
    {
        IWorldState mainWorldState = _processingScope.Resolve<IWorldState>();
        using (mainWorldState.BeginScope(parent))
        {
            Task? hintBalTask = block.BlockAccessList is not null && preWarmer.IsBalReadWarmingEnabled(spec)
                ? mainWorldState.HintBal(block.BlockAccessList)
                : null;
            preWarmer.PreWarmCaches(block, parent, spec).GetAwaiter().GetResult();
            hintBalTask?.GetAwaiter().GetResult();
        }
        return Task.CompletedTask;
    }

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
    /// Pool policy that captures the processing-thread flag when the prewarmer builds an env.
    /// </summary>
    private sealed class FlagCapturingPolicy(
        PrewarmerEnvFactory factory,
        PreBlockCaches caches,
        ManualResetEventSlim observed,
        Action<bool> capture)
        : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        private readonly ManualResetEventSlim _observed = observed;
        private readonly Action<bool> _capture = capture;
        private int _captured;

        public IReadOnlyTxProcessorSource Create()
        {
            IReadOnlyTxProcessorSource inner = factory.Create(caches);
            return new CapturingEnv(inner, this);
        }

        public bool Return(IReadOnlyTxProcessorSource obj) => true;

        private sealed class CapturingEnv(
            IReadOnlyTxProcessorSource inner,
            FlagCapturingPolicy owner)
            : IReadOnlyTxProcessorSource
        {
            public IReadOnlyTxProcessingScope Build(BlockHeader? baseBlock)
            {
                if (Interlocked.CompareExchange(ref owner._captured, 1, 0) == 0)
                {
                    owner._capture(ProcessingThread.IsBlockProcessingThread);
                    owner._observed.Set();
                }

                return inner.Build(baseBlock);
            }

            public void Dispose() => inner.Dispose();
        }
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

    [Test]
    public async Task PreWarmCaches_SkipStarted_SkipsTransactionsMainThreadHasStarted()
    {
        PrewarmerEnvFactory envFactory = _processingScope.Resolve<PrewarmerEnvFactory>();
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        NodeStorageCache nodeStorageCache = _processingScope.Resolve<NodeStorageCache>();
        Block block = BuildTwoSenderBlock();

        // Control: main thread has not started any tx, so every tx is speculatively warmed.
        int warmedWhenNoneStarted = 0;
        using (ManualResetEventSlim openGate = new(initialState: true))
        {
            WarmupCountingPolicy policy = new(envFactory, preBlockCaches, openGate, () => Interlocked.Increment(ref warmedWhenNoneStarted));
            using BlockCachePreWarmer preWarmer = new(policy, maxPoolSize: 10, concurrency: 2,
                parallelExecutionBatchRead: true, nodeStorageCache, preBlockCaches, LimboLogs.Instance);
            await RunPreWarmCaches(preWarmer, block, BuildParentHeader(), Osaka.Instance);
        }
        Assert.That(warmedWhenNoneStarted, Is.EqualTo(block.Transactions.Length),
            "with the main thread not started, all transactions must be speculatively warmed");

        // Skip: main thread reports it has started every tx (while warming is gated before building its scopes),
        // so all speculative warming must be skipped.
        int warmedWhenAllStarted = 0;
        using (ManualResetEventSlim gate = new(initialState: false))
        {
            WarmupCountingPolicy policy = new(envFactory, preBlockCaches, gate, () => Interlocked.Increment(ref warmedWhenAllStarted));
            using BlockCachePreWarmer preWarmer = new(policy, maxPoolSize: 10, concurrency: 2,
                parallelExecutionBatchRead: true, nodeStorageCache, preBlockCaches, LimboLogs.Instance);

            IWorldState mainWorldState = _processingScope.Resolve<IWorldState>();
            using (mainWorldState.BeginScope(BuildParentHeader()))
            {
                Task task = preWarmer.PreWarmCaches(block, BuildParentHeader(), Osaka.Instance);
                // Advance the prewarmer's view of main-thread progress past every tx while warming is gated at env.Build.
                for (int i = 0; i < block.Transactions.Length; i++)
                {
                    preWarmer.OnBeforeTxExecution();
                }
                gate.Set();
                task.GetAwaiter().GetResult();
            }
        }
        Assert.That(warmedWhenAllStarted, Is.EqualTo(0),
            "transactions the main thread has already started must not be speculatively warmed");
    }

    /// <summary>
    /// Only the main execution reports per-tx progress to the prewarmer; the prewarmer's own speculative envs
    /// must stay silent, otherwise they would advance its view of main-thread progress and suppress warming.
    /// The two are told apart by the injected <see cref="IPrewarmerState"/>.
    /// </summary>
    [TestCase(false, 0, TestName = "PrewarmerTxAdapter_MainExecution_ReportsTxProgress")]
    [TestCase(true, -1, TestName = "PrewarmerTxAdapter_PrewarmerEnv_DoesNotReportTxProgress")]
    public void PrewarmerTxAdapter_ReportsTxProgress_OnlyWhenNotPrewarmer(bool isPrewarmer, int expectedTxIndex)
    {
        PreBlockCaches preBlockCaches = _processingScope.Resolve<PreBlockCaches>();
        (BlockCachePreWarmer preWarmer, _, _) = CreatePreWarmer(maxPoolSize: 1);
        using (preWarmer)
        {
            PrewarmerTxAdapter adapter = new(
                new NoopTxProcessorAdapter(),
                preWarmer,
                new PrewarmerState(preBlockCaches, isPrewarmer));

            adapter.Execute(Build.A.Transaction.TestObject, NullTxTracer.Instance);

            Assert.That(preWarmer.MainThreadTxIndex, Is.EqualTo(expectedTxIndex));
        }
    }

    private sealed class NoopTxProcessorAdapter : ITransactionProcessorAdapter
    {
        public TransactionResult Execute(Transaction transaction, ITxTracer txTracer) => TransactionResult.Ok;
        public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext) { }
    }

    /// <summary>Gates scope construction and counts speculative Warmup executions.</summary>
    private sealed class WarmupCountingPolicy(
        PrewarmerEnvFactory factory,
        PreBlockCaches caches,
        ManualResetEventSlim gate,
        Action onWarmup)
        : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
    {
        public IReadOnlyTxProcessorSource Create() => new CountingEnv(factory.Create(caches), gate, onWarmup);

        public bool Return(IReadOnlyTxProcessorSource obj) => true;

        private sealed class CountingEnv(IReadOnlyTxProcessorSource inner, ManualResetEventSlim gate, Action onWarmup) : IReadOnlyTxProcessorSource
        {
            public IReadOnlyTxProcessingScope Build(BlockHeader? baseBlock)
            {
                gate.Wait();
                return new CountingScope(inner.Build(baseBlock), onWarmup);
            }

            public void Dispose() => inner.Dispose();
        }

        private sealed class CountingScope(IReadOnlyTxProcessingScope inner, Action onWarmup) : IReadOnlyTxProcessingScope
        {
            private readonly CountingTxProcessor _processor = new(inner.TransactionProcessor, onWarmup);
            public Nethermind.Evm.TransactionProcessing.ITransactionProcessor TransactionProcessor => _processor;
            public IWorldState WorldState => inner.WorldState;
            public void Dispose() => inner.Dispose();
        }

        private sealed class CountingTxProcessor(Nethermind.Evm.TransactionProcessing.ITransactionProcessor inner, Action onWarmup)
            : Nethermind.Evm.TransactionProcessing.ITransactionProcessor
        {
            public Nethermind.Evm.TransactionProcessing.TransactionResult Process(Transaction transaction, Nethermind.Evm.Tracing.ITxTracer txTracer, Nethermind.Evm.TransactionProcessing.ExecutionOptions options)
            {
                if ((options & Nethermind.Evm.TransactionProcessing.ExecutionOptions.Warmup) != 0) onWarmup();
                return inner.Process(transaction, txTracer, options);
            }

            public void SetBlockExecutionContext(BlockHeader blockHeader) => inner.SetBlockExecutionContext(blockHeader);
            public void SetBlockExecutionContext(in Nethermind.Evm.BlockExecutionContext blockExecutionContext) => inner.SetBlockExecutionContext(in blockExecutionContext);
        }
    }
}
