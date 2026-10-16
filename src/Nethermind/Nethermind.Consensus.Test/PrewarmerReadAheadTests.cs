// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

/// <summary>
/// The in-flight read-ahead: storage discovery for the transaction block processing is executing, started by block
/// processing's reads past the pre-block caches, seeded with what the block's earlier transactions wrote, retried with
/// a zero placeholder, and stopped and joined with the block's session.
/// </summary>
[TestFixture]
public class PrewarmerReadAheadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly Address LongLoop = new("0x00000000000000000000000000000000004e4d3f");
    private static readonly StorageCell LongLoopFinished = new(LongLoop, 2);
    // Reads slot 1, counts down from 2^32 - 1, then reads slot 2.
    private static readonly byte[] LongLoopCode =
    [
        0x60, 0x01, 0x54, 0x50, 0x63, 0xFF, 0xFF, 0xFF, 0xFF, 0x5B, 0x60, 0x01, 0x90, 0x03, 0x80, 0x60, 0x09, 0x57, 0x50,
        0x60, 0x02, 0x54, 0x50, 0x00
    ];
    private static readonly IReleaseSpec Spec = Osaka.Instance;
    // Gas for minutes of the long loop even in Release; a block that large admits the run to discovery's rounds.
    private const ulong LoopGas = 200_000_000_000;
    // Reverts at once, with no data.
    private static readonly Address Reverts = new("0x00000000000000000000000000000000004e4d40");
    private static readonly byte[] RevertsCode = [0x60, 0x00, 0x60, 0x00, 0xFD];
    // Above the gas from which the block's up-front discovery selects a transaction.
    private const ulong UpFrontGas = 12_000_000;

    private IContainer _container = null!;
    private ILifetimeScope _scope = null!;
    private BlockHeader _parent = null!;
    private PreBlockCaches _caches = null!;

    [SetUp]
    public void Setup()
    {
        KnownParents parents = new();
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Spec))
            .AddSingleton<IStateHeaderProvider>(parents)
            .Build();

        IMainProcessingModule[] mainModules = _container.Resolve<IMainProcessingModule[]>();
        IWorldStateManager worldStateManager = _container.Resolve<IWorldStateManager>();
        IWorldStateScopeProvider scopeProvider = worldStateManager.GlobalWorldState;
        _scope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(worldStateManager).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(mainModules);
        });

        IWorldState worldState = _scope.Resolve<IWorldState>();
        Hash256 genesisRoot;
        using (worldState.BeginScope(IWorldState.PreGenesis))
        {
            foreach (Address funded in (Address[])[TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD])
            {
                worldState.CreateAccount(funded, 1_000.Ether);
            }

            ColdTailContracts.Deploy(worldState, Spec);
            worldState.CreateAccount(LongLoop, 0);
            worldState.InsertCode(LongLoop, Keccak.Compute(LongLoopCode), LongLoopCode, Spec);
            worldState.Set(new StorageCell(LongLoop, 1), 1);
            worldState.Set(LongLoopFinished, 1);
            worldState.CreateAccount(Reverts, 0);
            worldState.InsertCode(Reverts, Keccak.Compute(RevertsCode), RevertsCode, Spec);
            worldState.Commit(Spec);
            worldState.CommitTree(0);
            genesisRoot = worldState.StateRoot;
        }

        _parent = parents.Add(Build.A.BlockHeader.WithNumber(0).WithStateRoot(genesisRoot).WithGasLimit(30_000_000)
            .WithHash(Build.A.BlockHeader.TestObject.ParentHash!).TestObject);
        _caches = _scope.Resolve<PreBlockCaches>();
    }

    [TearDown]
    public void TearDown()
    {
        _scope?.Dispose();
        _container?.Dispose();
    }

    [Test]
    public void Discovery_keeps_a_candidate_block_processing_is_executing()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true);
        Transaction walk = WalkerCall(TestItem.PrivateKeyA, 0, 0x4e4d, 4);
        Block block = BuildBlock(walk);
        preWarmer.OnBeforeTxExecution();

        preWarmer.DiscoverAndWarmStorage([(0, walk)], block, Spec, null, CancellationToken.None, readAhead: true);

        Assert.That(_caches.StorageCache.TryGetValue(new StorageCell(ColdTailContracts.Walker, 0x4e4d + 3), out _), Is.True,
            "a transaction block processing is executing is still worth reading ahead of it");
    }

    [Test]
    public void Discovery_drops_a_candidate_block_processing_has_finished()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true);
        Transaction walk = WalkerCall(TestItem.PrivateKeyA, 0, 0x4e4d, 4);
        Block block = BuildBlock(walk, Transfer(TestItem.PrivateKeyB, 0));
        preWarmer.OnBeforeTxExecution();
        preWarmer.OnBeforeTxExecution();

        preWarmer.DiscoverAndWarmStorage([(0, walk)], block, Spec, null, CancellationToken.None, readAhead: true);

        Assert.That(_caches.StorageCache.TryGetValue(new StorageCell(ColdTailContracts.Walker, 0x4e4d), out _), Is.False);
    }

    [Test]
    public void Switched_off_discovery_leaves_a_candidate_block_processing_has_started()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: false);
        Transaction walk = WalkerCall(TestItem.PrivateKeyA, 0, 0x4e4d, 4);
        Block block = BuildBlock(walk);
        preWarmer.OnBeforeTxExecution();

        preWarmer.DiscoverAndWarmStorage([(0, walk)], block, Spec, null, CancellationToken.None);

        Assert.That(_caches.StorageCache.TryGetValue(new StorageCell(ColdTailContracts.Walker, 0x4e4d), out _), Is.False);
    }

    [Test]
    public void A_run_stops_once_block_processing_finishes_its_transaction()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true);
        Transaction loop = Build.A.Transaction.WithGasLimit(LoopGas).WithTo(LongLoop).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block block = BuildBlock(LoopGas, loop, Transfer(TestItem.PrivateKeyB, 0));
        using CancellationTokenSource cancellation = new();

        Task discovery = Task.Run(() => preWarmer.DiscoverAndWarmStorage([(0, loop)], block, Spec, null, cancellation.Token, readAhead: true));
        try
        {
            Assert.That(SpinWait.SpinUntil(() => _caches.StateCache.TryGetValue(LongLoop, out _), Timeout), Is.True,
                "precondition: the run has entered the loop's call");
            preWarmer.OnBeforeTxExecution();
            Assert.That(discovery.Wait(TimeSpan.FromSeconds(1)), Is.False, "a run of the transaction block processing is executing goes on");
            preWarmer.OnBeforeTxExecution();

            Assert.That(discovery.Wait(TimeSpan.FromSeconds(5)), Is.True, "the run stops once block processing has finished the transaction");
            Assert.That(_caches.StorageCache.TryGetValue(LongLoopFinished, out _), Is.False);
        }
        finally
        {
            cancellation.Cancel();
            discovery.Wait(Timeout);
        }
    }

    /// <summary>
    /// A claim reverts at its first slot unless that slot is still zero, so a placeholder of 1 or 0x0101..01 finds one
    /// slot per round; a run with 0 finds the whole range in one.
    /// </summary>
    [Test]
    public void A_run_that_reverts_with_every_placeholder_is_repeated_with_zero()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true);
        Transaction claim = BatchClaim(TestItem.PrivateKeyA, 0, 8);
        Block block = BuildBlock(claim);

        preWarmer.DiscoverAndWarmStorage([(0, claim)], block, Spec, null, CancellationToken.None, readAhead: true);

        StorageCell last = ColdTailContracts.BatchSlot(ColdTailContracts.BatchCounter, 7);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_caches.StorageCache.TryGetValue(in last, out UInt256 value), Is.True, "the last slot of the claim is found");
            Assert.That(value, Is.EqualTo(UInt256.Zero), "at its parent value, never the placeholder");
        }
    }

    /// <summary>
    /// The second claim of a sender reads the range the counter the first one moved points at. Seeded with the counter
    /// block processing left, the read-ahead finds that range; the caches get its slots, and the counter, only at their
    /// parent values, and the seeds are committed without roots.
    /// </summary>
    [Test]
    public void A_run_is_seeded_with_what_the_blocks_earlier_transactions_wrote([Values] bool seeded, [Values] bool cachedCounter)
    {
        CommitCountingEnvs envs = new(_scope.Resolve<PrewarmerEnvFactory>(), _caches);
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, policy: envs);
        Transaction first = BatchClaim(TestItem.PrivateKeyA, 0, 6);
        Transaction second = BatchClaim(TestItem.PrivateKeyA, 1, 6);
        Block block = BuildBlock(first, second);
        BlockFootprints footprints = new(block);
        UInt256 moved = ColdTailContracts.BatchCounter + 6;
        preWarmer.OnBeforeTxExecution();
        footprints.QueueExecuted(0, new[] { (new StorageCell(ColdTailContracts.Batch, 0), moved) });
        footprints.ApplyExecuted();
        preWarmer.OnBeforeTxExecution();
        if (cachedCounter) _caches.StorageCache.Set(new StorageCell(ColdTailContracts.Batch, 0), ColdTailContracts.BatchCounter);

        preWarmer.DiscoverAndWarmStorage([(1, second)], block, Spec, null, CancellationToken.None, footprints: seeded ? footprints : null, readAhead: true);

        bool found = _caches.StorageCache.TryGetValue(ColdTailContracts.BatchSlot(moved, 5), out UInt256 value);
        bool counterCached = _caches.StorageCache.TryGetValue(new StorageCell(ColdTailContracts.Batch, 0), out UInt256 counter);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.EqualTo(seeded), "the second claim's own range is found only on the counter the first one left");
            if (found) Assert.That(value, Is.EqualTo(UInt256.Zero));
            if (counterCached) Assert.That(counter, Is.EqualTo(ColdTailContracts.BatchCounter), "the seed never reaches the caches");
            Assert.That(envs.CommitsWithRoots, Is.Zero, "a seeded run commits nothing with roots");
            Assert.That(envs.CommitsWithoutRoots, seeded ? Is.GreaterThan(0) : Is.Zero);
            Assert.That(footprints.Get(0), Is.Null);
            Assert.That(footprints.Get(1), Is.Null, "a read-ahead records no footprint");
        }
    }

    /// <summary>Seeds may be wrong, as a footprint's prediction can be; the caches still only ever get parent values.</summary>
    [Test]
    public void A_wrong_seed_costs_precision_never_a_cached_value()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true);
        Transaction first = BatchClaim(TestItem.PrivateKeyA, 0, 6);
        Transaction second = BatchClaim(TestItem.PrivateKeyA, 1, 6);
        Block block = BuildBlock(first, second);
        BlockFootprints footprints = new(block);
        preWarmer.OnBeforeTxExecution();
        // One behind what block processing would leave, and a slot of the range the second claim checks set to non-zero.
        footprints.QueueExecuted(0, new[]
        {
            (new StorageCell(ColdTailContracts.Batch, 0), ColdTailContracts.BatchCounter + 5),
            (ColdTailContracts.BatchSlot(ColdTailContracts.BatchCounter + 5, 1), (UInt256)1)
        });
        footprints.ApplyExecuted();
        preWarmer.OnBeforeTxExecution();

        preWarmer.DiscoverAndWarmStorage([(1, second)], block, Spec, null, CancellationToken.None, footprints: footprints, readAhead: true);

        Assert.That(EntriesNotAtTheParent(), Is.Empty);
    }

    /// <summary>Only a block that reads ahead seeds its runs; in any other the footprints the block has do not reach them.</summary>
    [Test]
    public void Only_a_block_that_reads_ahead_seeds_its_runs([Values] bool readAhead)
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true);
        Transaction second = BatchClaim(TestItem.PrivateKeyA, 1, 6);
        Block block = BuildBlock(BatchClaim(TestItem.PrivateKeyA, 0, 6), second);
        BlockFootprints footprints = new(block);
        UInt256 moved = ColdTailContracts.BatchCounter + 6;
        // Block processing is on the first claim, so the second is discovered with or without the read-ahead.
        preWarmer.OnBeforeTxExecution();
        footprints.QueueExecuted(0, new[] { (new StorageCell(ColdTailContracts.Batch, 0), moved) });
        footprints.ApplyExecuted();

        preWarmer.DiscoverAndWarmStorage([(1, second)], block, Spec, null, CancellationToken.None, footprints: footprints, readAhead: readAhead);

        // The first slot past the moved counter: a run reads it only on the seed, before its zero check reverts.
        Assert.That(_caches.StorageCache.TryGetValue(ColdTailContracts.BatchSlot(moved, 0), out _), Is.EqualTo(readAhead));
    }

    [Test]
    public void Block_processings_cold_reads_start_one_read_ahead_of_the_transaction_it_executes()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, coldReads: 3);
        Block block = BuildBlock(WalkerCall(TestItem.PrivateKeyA, 0, 0x5000, 6), Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0));
        using IDisposable session = OpenSession(preWarmer, block);
        _caches.StorageCache.Clear();

        _caches.ReportConsumerColdRead();
        preWarmer.OnBeforeTxExecution();
        _caches.ReportConsumerColdRead();
        _caches.ReportConsumerColdRead();
        int belowThreshold = preWarmer.ReadAheadsStarted;
        _caches.ReportConsumerColdRead();
        int atThreshold = preWarmer.ReadAheadsStarted;
        for (int i = 0; i < 10; i++) _caches.ReportConsumerColdRead();
        bool idle = SpinWait.SpinUntil(() => preWarmer.ReadAheadsRunning == 0, Timeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(belowThreshold, Is.Zero, "reads before the first transaction and below the threshold start nothing");
            Assert.That(atThreshold, Is.EqualTo(1));
            Assert.That(preWarmer.ReadAheadsStarted, Is.EqualTo(1), "once per transaction");
            Assert.That(idle, Is.True);
            Assert.That(_caches.StorageCache.TryGetValue(new StorageCell(ColdTailContracts.Walker, 0x5005), out _), Is.True,
                "the read-ahead read the transaction's slots");
        }
    }

    /// <summary>
    /// The block's token is cancelled once its transactions are executed. The reads that follow, of the work that
    /// finalises the block, belong to no transaction, and may come from other threads: they are not counted.
    /// </summary>
    [Test]
    public void Reads_after_the_transactions_are_executed_are_not_counted()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, coldReads: 3);
        Block block = BuildBlock(WalkerCall(TestItem.PrivateKeyA, 0, 0x5000, 6), Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0));
        using CancellationTokenSource transactionsExecuted = new();
        using IDisposable session = OpenSession(preWarmer, block, transactionsExecuted.Token);
        _caches.StorageCache.Clear();

        preWarmer.OnBeforeTxExecution();
        for (int i = 0; i < 3; i++) _caches.ReportConsumerColdRead();
        bool idle = SpinWait.SpinUntil(() => preWarmer.ReadAheadsRunning == 0, Timeout);
        long lateMisses = Blockchain.Metrics.PrewarmInFlightLateMisses;
        _caches.ReportConsumerColdRead();
        long lateWhileExecuting = Blockchain.Metrics.PrewarmInFlightLateMisses - lateMisses;

        transactionsExecuted.Cancel();
        lateMisses = Blockchain.Metrics.PrewarmInFlightLateMisses;
        for (int i = 0; i < 2 * 3; i++) _caches.ReportConsumerColdRead();
        long lateAfterwards = Blockchain.Metrics.PrewarmInFlightLateMisses - lateMisses;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(idle, Is.True);
            Assert.That(lateWhileExecuting, Is.EqualTo(1), "precondition: a read past the started read-ahead counts as a late miss");
            Assert.That(lateAfterwards, Is.Zero, "a read after the transactions is no late miss of the last one");
            Assert.That(preWarmer.ReadAheadsStarted, Is.EqualTo(1), "nor does it start a read-ahead");
        }
    }

    /// <summary>
    /// In a session, the read-ahead of a sender's second claim is seeded with the counter block processing reported its
    /// first claim left, and finds the range that counter points at.
    /// </summary>
    [Test]
    public void A_read_ahead_is_seeded_with_what_block_processing_reported_writing()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, coldReads: 1, handoff: true);
        Block block = BuildBlock(BatchClaim(TestItem.PrivateKeyA, 0, 6), BatchClaim(TestItem.PrivateKeyA, 1, 6), Transfer(TestItem.PrivateKeyB, 0));
        UInt256 moved = ColdTailContracts.BatchCounter + 6;
        using IDisposable session = OpenSession(preWarmer, block);
        BlockFootprints? footprints = preWarmer.Footprints;
        Assert.That(footprints, Is.Not.Null, "precondition: the block takes block processing's writes");
        _caches.StorageCache.Clear();

        preWarmer.OnBeforeTxExecution();
        preWarmer.ReportExecutedWrites(new[] { (new StorageCell(ColdTailContracts.Batch, 0), moved) });
        footprints!.ApplyExecuted();
        preWarmer.OnBeforeTxExecution();
        _caches.ReportConsumerColdRead();
        bool idle = SpinWait.SpinUntil(() => preWarmer.ReadAheadsRunning == 0, Timeout);

        bool found = _caches.StorageCache.TryGetValue(ColdTailContracts.BatchSlot(moved, 5), out UInt256 value);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preWarmer.ReadAheadsStarted, Is.EqualTo(1), "precondition: block processing's read started the read-ahead");
            Assert.That(idle, Is.True);
            Assert.That(found, Is.True, "the range the reported counter points at is found");
            if (found) Assert.That(value, Is.EqualTo(UInt256.Zero), "at its parent value");
        }
    }

    [Test]
    public void The_sessions_end_stops_and_joins_its_read_ahead()
    {
        using GatedEnvs envs = new(_scope.Resolve<PrewarmerEnvFactory>(), _caches);
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, policy: envs);
        // A creation whose init code loops for minutes: the up-front discovery takes no creation, so the read-ahead runs it.
        Transaction loop = Build.A.Transaction.WithGasLimit(LoopGas).WithCode(LongLoopCode).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block block = BuildBlock(LoopGas, loop, Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0));
        IWorldState worldState = _scope.Resolve<IWorldState>();
        using IDisposable consumer = worldState.BeginScope(_parent);
        IDisposable session = preWarmer.PreWarmCaches(block, _parent, Spec)!;
        Assert.That(session, Is.Not.Null);
        Task? end = null;
        try
        {
            envs.Armed = true;
            preWarmer.OnBeforeTxExecution();
            _caches.ReportConsumerColdRead();
            Assert.That(envs.Entered.Wait(Timeout), Is.True, "the read-ahead owns a parent scope before cancellation");

            end = Task.Run(session.Dispose);
            Assert.That(SpinWait.SpinUntil(() => _caches.ColdReadObserver is null, Timeout), Is.True, "the session has entered Close");
            bool endedBeforeRelease = end.Wait(TimeSpan.FromSeconds(1));
            envs.Release.Set();
            Assert.That(envs.Returned.Wait(Timeout), Is.True, "the read-ahead returns its environment after cancellation");
            Assert.That(end.Wait(Timeout), Is.True, "the session joins the released read-ahead");

            int accounts = _caches.StateCache.SnapshotForTests().Count;
            int slots = _caches.StorageCache.SnapshotForTests().Count;
            _caches.ReportConsumerColdRead();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(endedBeforeRelease, Is.False, "cancellation alone does not join a read-ahead holding its parent scope");
                Assert.That(_caches.ColdReadObserver, Is.Null, "nothing listens to block processing's reads past the session");
                Assert.That(preWarmer.ReadAheadsStarted, Is.EqualTo(1), "no read-ahead starts past the session");
                Assert.That(_caches.StateCache.SnapshotForTests(), Has.Count.EqualTo(accounts), "nothing is written past the session");
                Assert.That(_caches.StorageCache.SnapshotForTests(), Has.Count.EqualTo(slots), "nothing is written past the session");
            }
        }
        finally
        {
            envs.Release.Set();
            if (envs.Entered.IsSet) envs.Returned.Wait(Timeout);
            end?.Wait(Timeout);
            session.Dispose();
        }
    }

    [Test]
    public void A_read_ahead_that_fails_leaves_block_processing_alone()
    {
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, policy: new ThrowingEnvs());
        Block block = BuildBlock(WalkerCall(TestItem.PrivateKeyA, 0, 0x5000, 6), Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0));
        IWorldState worldState = _scope.Resolve<IWorldState>();
        using (worldState.BeginScope(_parent))
        {
            IDisposable? session = preWarmer.PreWarmCaches(block, _parent, Spec);
            preWarmer.OnBeforeTxExecution();

            Assert.DoesNotThrow(_caches.ReportConsumerColdRead);
            Assert.That(SpinWait.SpinUntil(() => preWarmer.ReadAheadsRunning == 0, Timeout), Is.True);
            Assert.DoesNotThrow(() => session?.Dispose());
            Assert.That(preWarmer.ReadAheadsStarted, Is.EqualTo(1));
        }
    }

    [Test]
    public void Switched_off_it_listens_to_no_read([Values] bool readAhead)
    {
        BlocksConfig config = new()
        {
            PreWarming = PreWarmMode.Block,
            PreWarmStateConcurrency = 2,
            PreWarmInFlightReadAhead = readAhead
        };
        using BlockCachePreWarmer preWarmer = new(_scope.Resolve<PrewarmerEnvFactory>(), config, _scope.Resolve<NodeStorageCache>(), _caches, LimboLogs.Instance);
        Block block = BuildBlock(WalkerCall(TestItem.PrivateKeyA, 0, 0x5000, 6), Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0));
        bool listening;
        using (OpenSession(preWarmer, block))
        {
            listening = _caches.ColdReadObserver is not null;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(listening, Is.EqualTo(readAhead));
            Assert.That(_caches.ColdReadObserver, Is.Null);
        }
    }

    /// <summary>
    /// A block being produced does not read ahead, the read-ahead switched on or not: its discovery runs as it does with
    /// the read-ahead off. Here, a run that reverts with the first placeholder is not repeated with zero.
    /// </summary>
    [Test]
    public void Only_a_block_that_reads_ahead_repeats_a_reverted_run_with_zero([Values] bool produced)
    {
        using CaptureWatchingEnvs envs = new(_scope.Resolve<PrewarmerEnvFactory>(), _caches);
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, policy: envs);
        Block block = BuildBlock(Call(TestItem.PrivateKeyA, 0, Reverts, UpFrontGas, []), Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0));
        if (produced) block = AsProduced(block);

        bool listening;
        using (OpenSession(preWarmer, block))
        {
            listening = _caches.ColdReadObserver is not null;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(listening, Is.EqualTo(!produced));
            Assert.That(envs.Placeholders, produced ? Is.EqualTo(new[] { UInt256.One }) : Is.EqualTo(new[] { UInt256.One, UInt256.Zero }));
        }
    }

    /// <summary>
    /// Discovery leaves a transaction block processing has started, unless the block reads ahead: then it leaves only
    /// one block processing has finished. The transaction's sender lands only once block processing has started it.
    /// </summary>
    [Test]
    public void Only_a_block_that_reads_ahead_discovers_the_transaction_block_processing_is_executing([Values] bool produced)
    {
        SenderRecovery recovery = new();
        using CaptureWatchingEnvs envs = new(_scope.Resolve<PrewarmerEnvFactory>(), _caches);
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, policy: envs, senderRecovery: recovery);
        Transaction heavy = Call(TestItem.PrivateKeyA, 0, Reverts, UpFrontGas, []);
        Address sender = heavy.SenderAddress!;
        heavy.SenderAddress = null;
        Block block = BuildBlock(heavy, Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0));
        if (produced) block = AsProduced(block);

        IWorldState worldState = _scope.Resolve<IWorldState>();
        using (worldState.BeginScope(_parent))
        {
            using IDisposable session = preWarmer.PreWarmCaches(block, _parent, Spec)!;
            preWarmer.OnBeforeTxExecution();
            recovery.Publish(heavy, sender);
            ((PrewarmingSession)session).WaitForCompletion();
        }

        Assert.That(envs.Placeholders, produced ? Is.Empty : Is.Not.Empty);
    }

    /// <summary>
    /// In a block that reads ahead, a discovery run stops once block processing has finished its transaction; in any
    /// other it runs on until the block's token is cancelled.
    /// </summary>
    [Test]
    public void Only_a_block_that_reads_ahead_stops_a_run_once_block_processing_finishes_its_transaction([Values] bool produced)
    {
        using CaptureWatchingEnvs envs = new(_scope.Resolve<PrewarmerEnvFactory>(), _caches);
        // Room for discovery's run beside the warm of the same loop.
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, policy: envs, concurrency: 4);
        Transaction loop = Build.A.Transaction.WithGasLimit(LoopGas).WithTo(LongLoop).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Block block = BuildBlock(LoopGas, loop, Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0));
        if (produced) block = AsProduced(block);

        IWorldState worldState = _scope.Resolve<IWorldState>();
        using IDisposable consumer = worldState.BeginScope(_parent);
        IDisposable session = preWarmer.PreWarmCaches(block, _parent, Spec)!;
        try
        {
            Assert.That(envs.Captured.Wait(Timeout), Is.True, "precondition: discovery runs the loop");
            preWarmer.OnBeforeTxExecution();
            preWarmer.OnBeforeTxExecution();

            bool stopped = envs.CaptureReturned.Wait(produced ? TimeSpan.FromSeconds(1) : Timeout);

            Assert.That(stopped, Is.EqualTo(!produced), "only a block that reads ahead stops the run before its token");
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>A block access list already enumerates the block's reads: no discovery runs, and nothing reads ahead.</summary>
    [Test]
    public void A_block_with_an_access_list_neither_reads_ahead_nor_discovers()
    {
        using CaptureWatchingEnvs envs = new(_scope.Resolve<PrewarmerEnvFactory>(), _caches);
        using BlockCachePreWarmer preWarmer = CreatePreWarmer(readAhead: true, policy: envs);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1).WithGasLimit(30_000_000)
            .WithTransactions(Call(TestItem.PrivateKeyA, 0, Reverts, UpFrontGas, []), Transfer(TestItem.PrivateKeyB, 0), Transfer(TestItem.PrivateKeyC, 0))
            .WithBlockAccessList(bal)
            .TestObject;
        Assert.That(preWarmer.IsBalReadWarmingEnabled(Amsterdam.Instance), Is.True, "precondition: the access list is read");

        bool listening;
        IWorldState worldState = _scope.Resolve<IWorldState>();
        using (worldState.BeginScope(_parent))
        {
            using IDisposable session = preWarmer.PreWarmCaches(block, _parent, Amsterdam.Instance)!;
            listening = _caches.ColdReadObserver is not null;
            ((PrewarmingSession)session).WaitForCompletion();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(listening, Is.False);
            Assert.That(envs.Placeholders, Is.Empty);
        }
    }

    [Test]
    public void The_config_defaults_start_at_the_thirty_second_cold_read()
    {
        BlocksConfig config = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.PreWarmInFlightReadAhead, Is.True);
            Assert.That(config.PreWarmInFlightReadAheadColdReads, Is.EqualTo(BlockCachePreWarmer.DefaultReadAheadColdReads));
            Assert.That(BlockCachePreWarmer.DefaultReadAheadColdReads, Is.EqualTo(32));
        }
    }

    /// <summary>Opens the block's session and waits for its warm pass, leaving the session, and its read-ahead, open.</summary>
    private IDisposable OpenSession(BlockCachePreWarmer preWarmer, Block block, CancellationToken token = default)
    {
        IWorldState worldState = _scope.Resolve<IWorldState>();
        using (worldState.BeginScope(_parent))
        {
            IDisposable? session = preWarmer.PreWarmCaches(block, _parent, Spec, token);
            Assert.That(session, Is.Not.Null, "precondition: the block is warmed");
            ((PrewarmingSession)session!).WaitForCompletion();
            return session;
        }
    }

    private List<string> EntriesNotAtTheParent()
    {
        IStateReader reader = _container.Resolve<IWorldStateManager>().GlobalStateReader;
        List<string> wrong = [];
        foreach ((StorageCell cell, UInt256 value) in _caches.StorageCache.SnapshotForTests())
        {
            reader.GetStorage(_parent, cell.Address, in cell.Index, out UInt256 parent);
            if (value != parent) wrong.Add($"{cell}: {value} cached, {parent} at the parent");
        }

        return wrong;
    }

    private BlockCachePreWarmer CreatePreWarmer(bool readAhead, int coldReads = 0, IPooledObjectPolicy<IPrewarmerEnv>? policy = null,
        int concurrency = 2, ISenderRecoveryTracker? senderRecovery = null, bool handoff = false) =>
        new(policy ?? new BlockCachePreWarmer.ReadOnlyTxProcessingEnvPooledObjectPolicy(_scope.Resolve<PrewarmerEnvFactory>(), _caches),
            minPoolSize: 4,
            concurrency: concurrency,
            parallelExecutionBatchRead: true,
            _scope.Resolve<NodeStorageCache>(),
            _caches,
            LimboLogs.Instance,
            senderRecovery: senderRecovery,
            handoff: handoff,
            readAhead: readAhead,
            readAheadColdReads: coldReads);

    private static Block BuildBlock(params Transaction[] transactions) => BuildBlock(30_000_000, transactions);

    private static Block BuildBlock(ulong gasLimit, params Transaction[] transactions) =>
        Build.A.Block.WithNumber(1).WithGasLimit(gasLimit).WithTransactions(transactions).TestObject;

    private static Block AsProduced(Block block) => new BlockToProduce(block.Header, block.Transactions, block.Uncles, block.Withdrawals);

    private static Transaction WalkerCall(PrivateKey sender, ulong nonce, UInt256 start, int count) =>
        Call(sender, nonce, ColdTailContracts.Walker, 1_000_000, ColdTailContracts.Words(start, (ulong)count));

    private static Transaction BatchClaim(PrivateKey sender, ulong nonce, int count) =>
        Call(sender, nonce, ColdTailContracts.Batch, 2_100_000, ColdTailContracts.Word((ulong)count));

    private static Transaction Call(PrivateKey sender, ulong nonce, Address to, ulong gasLimit, byte[] data) =>
        Build.A.Transaction.WithNonce(nonce).WithTo(to).WithGasLimit(gasLimit).WithData(data).SignedAndResolved(sender).TestObject;

    private static Transaction Transfer(PrivateKey sender, ulong nonce) =>
        Build.A.Transaction.WithNonce(nonce).WithTo(TestItem.AddressF).WithValue(1.Wei).SignedAndResolved(sender).TestObject;

    private sealed class GatedEnvs(PrewarmerEnvFactory factory, PreBlockCaches caches) : IPooledObjectPolicy<IPrewarmerEnv>, IDisposable
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public readonly ManualResetEventSlim Returned = new();
        public volatile bool Armed;
        private int _claimed;

        public IPrewarmerEnv Create() => new Env(factory.Create(caches), this, caches);

        public bool Return(IPrewarmerEnv obj)
        {
            if (((Env)obj).Held) Returned.Set();
            return true;
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
            Returned.Dispose();
        }

        private sealed class Env(IPrewarmerEnv inner, GatedEnvs owner, PreBlockCaches caches) : IPrewarmerEnv
        {
            public bool Held;
            public ReadOnlySpan<IHasAccessList> SystemAccessLists => inner.SystemAccessLists;
            FootprintRecorder? IPrewarmerEnv.Recorder => inner.Recorder;

            public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) => inner.TryBuild(baseBlock, out scope);

            public bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
            {
                bool built = inner.TryBuildAtTarget(targetBlock, out scope);
                if (built && owner.Armed && caches.CurrentStorageReadCapture is not null && Interlocked.CompareExchange(ref owner._claimed, 1, 0) == 0)
                {
                    Held = true;
                    owner.Entered.Set();
                    if (!owner.Release.Wait(Timeout))
                    {
                        scope!.Dispose();
                        throw new TimeoutException("The read-ahead's parent scope was not released.");
                    }
                }

                return built;
            }

            public void Dispose() => inner.Dispose();
        }
    }

    /// <summary>
    /// Envs that note each discovery run, a scope built while a storage-read capture is open: its placeholder, the first
    /// such build, and the first return of an env that made one.
    /// </summary>
    private sealed class CaptureWatchingEnvs(PrewarmerEnvFactory factory, PreBlockCaches caches) : IPooledObjectPolicy<IPrewarmerEnv>, IDisposable
    {
        private readonly ConcurrentQueue<UInt256> _placeholders = new();
        public readonly ManualResetEventSlim Captured = new();
        public readonly ManualResetEventSlim CaptureReturned = new();

        public UInt256[] Placeholders => [.. _placeholders];

        public IPrewarmerEnv Create() => new Env(factory.Create(caches), this, caches);

        public bool Return(IPrewarmerEnv obj)
        {
            Env env = (Env)obj;
            if (env.Captured)
            {
                env.Captured = false;
                CaptureReturned.Set();
            }

            return true;
        }

        public void Dispose()
        {
            Captured.Dispose();
            CaptureReturned.Dispose();
        }

        private sealed class Env(IPrewarmerEnv inner, CaptureWatchingEnvs owner, PreBlockCaches caches) : IPrewarmerEnv
        {
            public bool Captured;
            public ReadOnlySpan<IHasAccessList> SystemAccessLists => inner.SystemAccessLists;
            FootprintRecorder? IPrewarmerEnv.Recorder => inner.Recorder;

            public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) => inner.TryBuild(baseBlock, out scope);

            public bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
            {
                if (caches.CurrentStorageReadCapture is { } capture)
                {
                    owner._placeholders.Enqueue(capture.Placeholder);
                    Captured = true;
                    owner.Captured.Set();
                }

                return inner.TryBuildAtTarget(targetBlock, out scope);
            }

            public void Dispose() => inner.Dispose();
        }
    }

    /// <summary>A sender recovery in flight that a test drives: a published sender moves the count and wakes the waiters.</summary>
    private sealed class SenderRecovery : ISenderRecoveryTracker, ISenderRecoveryProgress
    {
        private readonly object _gate = new();
        private int _recovered;

        public ISenderRecoveryProgress? GetInFlight(Transaction[] txs) => this;
        public int Recovered => Volatile.Read(ref _recovered);
        public bool IsCompleted => false;

        public bool WaitForCompletion(int millisecondsTimeout)
        {
            lock (_gate)
            {
                Monitor.Wait(_gate, millisecondsTimeout);
            }

            return false;
        }

        public void Publish(Transaction tx, Address sender)
        {
            tx.SenderAddress = sender;
            Interlocked.Increment(ref _recovered);
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }

    private sealed class KnownParents : IStateHeaderProvider
    {
        private readonly Dictionary<Hash256, BlockHeader> _headers = [];

        public BlockHeader Add(BlockHeader header)
        {
            _headers[header.Hash!] = header;
            return header;
        }

        public BlockHeader? FindParentHeader(BlockHeader target) => _headers.GetValueOrDefault(target.ParentHash!);
        public ulong FinalizedBlockNumber => 0;
        public BlockHeader? GetFinalizedHeader(ulong blockNumber) => null;
    }

    /// <summary>Envs whose scopes count the commits made through their world state, with roots and without.</summary>
    private sealed class CommitCountingEnvs(PrewarmerEnvFactory factory, PreBlockCaches caches) : IPooledObjectPolicy<IPrewarmerEnv>
    {
        public int CommitsWithRoots;
        public int CommitsWithoutRoots;

        public IPrewarmerEnv Create() => new Env(factory.Create(caches), this);

        public bool Return(IPrewarmerEnv obj) => true;

        private sealed class Env(IPrewarmerEnv inner, CommitCountingEnvs owner) : IPrewarmerEnv
        {
            public ReadOnlySpan<IHasAccessList> SystemAccessLists => inner.SystemAccessLists;

            FootprintRecorder? IPrewarmerEnv.Recorder => inner.Recorder;

            public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
            {
                scope = inner.TryBuild(baseBlock, out IReadOnlyTxProcessingScope? built) ? new Scope(built, owner) : null;
                return scope is not null;
            }

            public bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope)
            {
                scope = inner.TryBuildAtTarget(targetBlock, out IReadOnlyTxProcessingScope? built) ? new Scope(built, owner) : null;
                return scope is not null;
            }

            public void Dispose() => inner.Dispose();
        }

        private sealed class Scope(IReadOnlyTxProcessingScope inner, CommitCountingEnvs owner) : IReadOnlyTxProcessingScope
        {
            public Evm.TransactionProcessing.ITransactionProcessor TransactionProcessor => inner.TransactionProcessor;
            public IWorldState WorldState { get; } = new Counting(inner.WorldState, owner);
            public void Reset() => inner.Reset();
            public void Dispose() => inner.Dispose();
        }

        private sealed class Counting(IWorldState state, CommitCountingEnvs owner) : WorldStateDecorator(state)
        {
            public override void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true)
            {
                if (commitRoots) Interlocked.Increment(ref owner.CommitsWithRoots);
                else Interlocked.Increment(ref owner.CommitsWithoutRoots);
                base.Commit(releaseSpec, tracer, isGenesis, commitRoots);
            }
        }
    }

    /// <summary>Envs whose every scope fails to build.</summary>
    private sealed class ThrowingEnvs : IPooledObjectPolicy<IPrewarmerEnv>
    {
        public IPrewarmerEnv Create() => new Env();

        public bool Return(IPrewarmerEnv obj) => true;

        private sealed class Env : IPrewarmerEnv
        {
            public ReadOnlySpan<IHasAccessList> SystemAccessLists => [];

            public bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) =>
                throw new InvalidOperationException("The env fails to build.");

            public bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope) =>
                throw new InvalidOperationException("The env fails to build.");

            public void Dispose() { }
        }
    }
}
