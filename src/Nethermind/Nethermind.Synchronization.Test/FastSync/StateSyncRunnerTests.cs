// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync;

[TestFixture]
public class StateSyncRunnerTests : StateSyncFeedTestsBase
{
    private IBalHealing _healing = null!;
    private ISnapSyncRunner _snapSyncRunner = null!;
    private ISnapTrieFactory _snapTrieFactory = null!;
    private IStateSyncPivot _pivot = null!;

    [Test]
    public async Task Heals_round_by_round_and_finalizes_at_the_last_pivot()
    {
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();

        BlockHeader firstPivot = blockTree.FindHeader(10)!;
        BlockHeader secondPivot = blockTree.FindHeader(11)!;
        BlockHeader lastPivot = blockTree.FindHeader(12)!;
        SeedBals(container, secondPivot, lastPivot);

        _pivot.GetPivotHeader().Returns(secondPivot, lastPivot, lastPivot);
        _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(TestItem.KeccakA);
        // Each round must be applied on the root the previous one produced, so the second stub only matches
        // when the runner threads TestItem.KeccakB through.
        _healing.ApplyRange(TestItem.KeccakA, firstPivot, secondPivot, Arg.Any<CancellationToken>()).Returns((false, TestItem.KeccakB));
        _healing.ApplyRange(TestItem.KeccakB, secondPivot, lastPivot, Arg.Any<CancellationToken>()).Returns((false, lastPivot.StateRoot));

        await runner.RunBalHealing(firstPivot, default);

        _healing.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Keeps_retrying_while_no_peer_serves_the_bals()
    {
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();

        BlockHeader firstPivot = blockTree.FindHeader(10)!;
        BlockHeader roundPivot = blockTree.FindHeader(11)!;

        // Nothing seeded into the BAL store, and the test peers only speak snap/1, so the window can never
        // be fetched. Healing must keep asking rather than fail - cancellation is the only way out.
        // The timer is only a guard against a regression that never comes back for a second round; the
        // cancellation this test relies on is the deterministic one below.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        int rounds = 0;
        _pivot.GetPivotHeader().Returns(_ =>
        {
            if (++rounds == 2) cts.Cancel();
            return roundPivot;
        });
        _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(TestItem.KeccakA);

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunBalHealing(firstPivot, cts.Token));

        // Reaching the second round is the retry: the first fetch failure was swallowed, not surfaced.
        Assert.That(rounds, Is.EqualTo(2));
        _healing.DidNotReceive().ApplyRange(Arg.Any<Hash256>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>());
        _healing.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Retries_the_round_when_a_collected_bal_goes_missing()
    {
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();

        BlockHeader firstPivot = blockTree.FindHeader(10)!;
        BlockHeader lastPivot = blockTree.FindHeader(11)!;
        SeedBals(container, lastPivot);

        _pivot.GetPivotHeader().Returns(lastPivot);
        _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(TestItem.KeccakA);
        // Nothing was written when a BAL goes missing while collecting, so the same range must be applied again
        // rather than ending state sync.
        _healing.ApplyRange(TestItem.KeccakA, firstPivot, lastPivot, Arg.Any<CancellationToken>())
            .Returns((true, null), (false, lastPivot.StateRoot));

        await runner.RunBalHealing(firstPivot, default);

        _healing.Received(2).ApplyRange(TestItem.KeccakA, firstPivot, lastPivot, Arg.Any<CancellationToken>());
        _healing.Received(1).FinalizeSync(lastPivot);
    }

    [Test]
    public async Task Finalizes_only_once_missing_code_is_recovered()
    {
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();

        BlockHeader firstPivot = blockTree.FindHeader(10)!;
        BlockHeader lastPivot = blockTree.FindHeader(11)!;
        SeedBals(container, lastPivot);

        _pivot.GetPivotHeader().Returns(lastPivot);
        _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(TestItem.KeccakA);
        _healing.ApplyRange(TestItem.KeccakA, firstPivot, lastPivot, Arg.Any<CancellationToken>()).Returns((false, lastPivot.StateRoot));
        // No peer serves the missing bytecode on the first attempt, so finalizing must wait for the second.
        _healing.TryRecoverMissingCode(Arg.Any<CancellationToken>()).Returns(false, true);

        await runner.RunBalHealing(firstPivot, default);

        Received.InOrder(() =>
        {
            _healing.TryRecoverMissingCode(Arg.Any<CancellationToken>());
            _healing.TryRecoverMissingCode(Arg.Any<CancellationToken>());
            _healing.FinalizeSync(lastPivot);
        });
    }

    [TestCase(false, TestName = "Does_not_finalize_when_the_healed_root_does_not_match_the_pivot")]
    [TestCase(true, TestName = "Does_not_finalize_when_the_range_is_lost")]
    public async Task Does_not_finalize_when_healing_cannot_reach_the_pivot(bool rangeLost)
    {
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();

        BlockHeader firstPivot = blockTree.FindHeader(10)!;
        BlockHeader lastPivot = blockTree.FindHeader(11)!;
        SeedBals(container, lastPivot);

        _pivot.GetPivotHeader().Returns(lastPivot);
        _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(TestItem.KeccakA);
        Hash256? healedRoot = rangeLost ? null : TestItem.KeccakF;
        _healing.ApplyRange(TestItem.KeccakA, firstPivot, lastPivot, Arg.Any<CancellationToken>()).Returns((false, healedRoot));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunBalHealing(firstPivot, default));

        _healing.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public async Task Syncs_the_state_again_when_the_pivot_is_reorged_out()
    {
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();

        BlockHeader canonical = blockTree.FindHeader(10)!;
        // Same height, different hash: the block tree does not have it on the main chain.
        BlockHeader orphaned = Build.A.BlockHeader.WithNumber(10).TestObject;

        _pivot.GetPivotHeader().Returns(canonical);
        _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(canonical.StateRoot);
        _snapTrieFactory.LoadCheckpoint().Returns(null, CreateCheckpoint(orphaned));

        await runner.RunSnapSyncWithBalHealing(orphaned, default);

        // The first attempt healed onto an orphaned pivot, so it must be thrown away and synced again.
        await _snapSyncRunner.Received(2).Run(Arg.Any<CancellationToken>());
        _healing.Received(1).FinalizeSync(canonical);
        _snapTrieFactory.Received(2).SaveCheckpoint(null);
        _snapTrieFactory.DidNotReceive().SaveCheckpoint(Arg.Is<SnapCheckpoint?>(static c => c != null));
    }

    // Healing writes are not durable until flushed, so the checkpoint of the finished ranges has to be gone before the
    // first of them: a crash during healing must start the sync over.
    [Test]
    public async Task Deletes_the_checkpoint_before_healing_writes()
    {
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        BlockHeader pivot = container.Resolve<IBlockTree>().FindHeader(10)!;

        _pivot.GetPivotHeader().Returns(pivot);
        _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(pivot.StateRoot);
        _snapTrieFactory.LoadCheckpoint().Returns(CreateCheckpoint(pivot));

        await runner.RunSnapSyncWithBalHealing(pivot, default);

        Received.InOrder(() =>
        {
            _snapSyncRunner.Run(Arg.Any<CancellationToken>());
            _snapTrieFactory.SaveCheckpoint(null);
            _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>());
            _healing.FinalizeSync(pivot);
        });
        _snapTrieFactory.DidNotReceive().SaveCheckpoint(Arg.Is<SnapCheckpoint?>(static c => c != null));
    }

    // Trie-node healing skips a node already present by hash, so a crash during it must not resume ranges whose nodes
    // may be on disk without their flat entries.
    [Test]
    public async Task Deletes_the_checkpoint_before_trie_node_healing([Values] bool snapResume)
    {
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        ISyncConfig syncConfig = container.Resolve<ISyncConfig>();
        syncConfig.SnapSync = true;
        syncConfig.BalHealing = false;
        syncConfig.SnapResume = snapResume;
        BlockHeader pivot = container.Resolve<IBlockTree>().FindHeader(10)!;

        _pivot.GetPivotHeader().Returns(pivot);
        _snapTrieFactory.LoadCheckpoint().Returns(null, CreateCheckpoint(pivot));

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await runner.Run(timeout.Token);

        Assert.That(timeout.IsCancellationRequested, Is.False, "state sync finished");
        if (snapResume)
        {
            Received.InOrder(() =>
            {
                _snapSyncRunner.Run(Arg.Any<CancellationToken>());
                _snapTrieFactory.SaveCheckpoint(null);
                _pivot.UpdateHeaderForcefully();
            });
        }
        else
        {
            _snapTrieFactory.DidNotReceiveWithAnyArgs().SaveCheckpoint(default);
        }
    }

    // The snap sync runner can return normally on a shutdown during the state ranges, having saved the checkpoint of
    // the partial ranges, which must then stay as it is.
    [Test]
    public async Task Leaves_the_checkpoint_untouched_at_a_graceful_stop_during_the_ranges()
    {
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        BlockHeader pivot = container.Resolve<IBlockTree>().FindHeader(10)!;

        using CancellationTokenSource shutdown = new();
        _snapSyncRunner.Run(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            shutdown.Cancel();
            return Task.CompletedTask;
        });
        _snapTrieFactory.LoadCheckpoint().Returns(null, CreateCheckpoint(pivot));
        _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(static c =>
        {
            c.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return TestItem.KeccakA;
        });

        await Assert.ThatAsync(() => runner.RunSnapSyncWithBalHealing(pivot, shutdown.Token), Throws.InstanceOf<OperationCanceledException>());

        _snapTrieFactory.DidNotReceiveWithAnyArgs().SaveCheckpoint(default);
    }

    public enum HealingStop { DuringReassembly, DuringApply, AfterRound, AwaitingFinalPivot }

    // A graceful shutdown lands between the chunks healing writes, so once they are flushed the checkpoint of the
    // finished ranges is good to resume from again. With resuming off, nothing is checkpointed.
    [Test]
    public async Task Saves_the_checkpoint_again_at_a_graceful_stop_during_healing([Values] HealingStop stop, [Values] bool snapResume)
    {
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        container.Resolve<ISyncConfig>().SnapResume = snapResume;

        BlockHeader firstPivot = blockTree.FindHeader(10)!;
        BlockHeader lastPivot = blockTree.FindHeader(11)!;
        SeedBals(container, lastPivot);
        SnapCheckpoint checkpoint = CreateCheckpoint(firstPivot);
        _snapTrieFactory.LoadCheckpoint().Returns(checkpoint);
        _pivot.GetPivotHeader().Returns(lastPivot);

        using CancellationTokenSource shutdown = new();
        _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            StopAt(HealingStop.DuringReassembly, throws: true);
            return TestItem.KeccakA;
        });
        _healing.ApplyRange(TestItem.KeccakA, firstPivot, lastPivot, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            StopAt(HealingStop.DuringApply, throws: true);
            // A stop during the last chunk of a round returns its root, as the chunk is written whole.
            StopAt(HealingStop.AfterRound, throws: false);
            return (false, lastPivot.StateRoot);
        });
        _pivot.CanFinalize(lastPivot).Returns(_ =>
        {
            StopAt(HealingStop.AwaitingFinalPivot, throws: false);
            return stop != HealingStop.AwaitingFinalPivot;
        });

        await Assert.ThatAsync(() => runner.RunSnapSyncWithBalHealing(firstPivot, shutdown.Token), Throws.InstanceOf<OperationCanceledException>());

        _healing.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
        if (snapResume)
        {
            Received.InOrder(() =>
            {
                _snapTrieFactory.SaveCheckpoint(null);
                _snapTrieFactory.SaveCheckpoint(checkpoint);
            });
        }
        else
        {
            _snapTrieFactory.DidNotReceiveWithAnyArgs().SaveCheckpoint(default);
        }

        void StopAt(HealingStop at, bool throws)
        {
            if (stop != at) return;
            shutdown.Cancel();
            if (throws) shutdown.Token.ThrowIfCancellationRequested();
        }
    }

    public enum HealingFailure { Fresh, Resumed, ResumedAndRestarted }

    // A resumed sync heals state kept across a restart, so a failure to heal it falls back to syncing again from
    // scratch, once, which a fresh sync failing the same way cannot do.
    [Test]
    public async Task Starts_over_when_healing_a_resumed_sync_fails([Values] HealingFailure failure, [Values] bool rangeLost)
    {
        bool resumed = failure != HealingFailure.Fresh;
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();

        BlockHeader firstPivot = blockTree.FindHeader(10)!;
        BlockHeader lastPivot = blockTree.FindHeader(11)!;
        SeedBals(container, lastPivot);
        SnapCheckpoint checkpoint = CreateCheckpoint(firstPivot);
        // A fresh sync has no checkpoint until its ranges are done.
        if (resumed) _snapTrieFactory.LoadCheckpoint().Returns(checkpoint);
        else _snapTrieFactory.LoadCheckpoint().Returns(null, checkpoint);

        _pivot.GetPivotHeader().Returns(lastPivot);
        // The sync started over is at the last pivot already, so its reassembled root is final, unless it is wrong again.
        Hash256 restartedRoot = failure == HealingFailure.ResumedAndRestarted ? TestItem.KeccakA : lastPivot.StateRoot!;
        _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(TestItem.KeccakA, restartedRoot);
        _healing.ApplyRange(TestItem.KeccakA, firstPivot, lastPivot, Arg.Any<CancellationToken>()).Returns((false, rangeLost ? null : TestItem.KeccakF));

        // Only a regression that keeps starting over runs into the timeout.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        if (failure == HealingFailure.Resumed) await runner.RunSnapSyncWithBalHealing(firstPivot, timeout.Token);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunSnapSyncWithBalHealing(firstPivot, timeout.Token));

        await _snapSyncRunner.Received(resumed ? 2 : 1).Run(Arg.Any<CancellationToken>());
        _healing.Received(failure == HealingFailure.Resumed ? 1 : 0).FinalizeSync(lastPivot);
        _snapTrieFactory.DidNotReceive().SaveCheckpoint(Arg.Is<SnapCheckpoint?>(static c => c != null));
    }

    // Healing with block access lists replays from the first pivot, so a resumed sync has to keep the one its downloaded
    // ranges started from - unless that block left the canonical chain, which leaves nothing to resume, or the node was
    // offline for so long that healing the kept ranges would cost more than downloading them again. A static pivot
    // never moves, so its anchor is resumed however old it is.
    [TestCase(true, 24, false, true, TestName = "Resumes_from_the_checkpoint_anchor")]
    [TestCase(false, 0, false, false, TestName = "Starts_over_when_the_checkpoint_anchor_is_not_canonical")]
    [TestCase(true, 25, false, false, TestName = "Starts_over_when_the_checkpoint_anchor_is_too_old")]
    [TestCase(true, 240, true, true, TestName = "Resumes_from_an_old_checkpoint_anchor_at_a_static_pivot")]
    public void Takes_the_first_pivot_from_the_checkpoint_anchor(bool canonical, int anchorAgeHours, bool staticPivot, bool resumes)
    {
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        BlockHeader anchor = canonical
            ? blockTree.FindHeader(10)!
            : Build.A.BlockHeader.WithNumber(10).TestObject;
        if (staticPivot)
        {
            container.Resolve<ISyncConfig>().StaticSnapPivot = true;
            blockTree.SyncPivot = (anchor.Number, anchor.Hash!);
        }

        ((ManualTimestamper)container.Resolve<ITimestamper>()).Set(DateTimeOffset.FromUnixTimeSeconds((long)anchor.Timestamp).UtcDateTime.AddHours(anchorAgeHours));
        _snapTrieFactory.LoadCheckpoint().Returns(CreateCheckpoint(anchor));

        Assert.That(runner.GetResumedAnchor()?.Hash, Is.EqualTo(resumes ? anchor.Hash : null));
        _snapTrieFactory.Received(resumes ? 0 : 1).SaveCheckpoint(null);
    }

    private IContainer BuildRunnerContainer()
    {
        _healing = Substitute.For<IBalHealing>();
        _healing.TryRecoverMissingCode(Arg.Any<CancellationToken>()).Returns(true);
        _snapSyncRunner = Substitute.For<ISnapSyncRunner>();
        _snapTrieFactory = Substitute.For<ISnapTrieFactory>();

        _pivot = Substitute.For<IStateSyncPivot>();
        _pivot.UpdatedStorages.Returns([]);
        _pivot.CanFinalize(Arg.Any<BlockHeader>()).Returns(true);

        // No peer here can serve BALs, so waiting on allocation only slows the retry test down.
        return PrepareDownloader(new RemoteDbContext(_logManager), syncDispatcherAllocateTimeoutMs: 0, configureBuilder: builder => builder
            .AddSingleton<IBalHealing>(_healing)
            .AddSingleton<ISnapSyncRunner>(_snapSyncRunner)
            .AddSingleton<ISnapTrieFactory>(_snapTrieFactory)
            .AddSingleton<IStateSyncPivot>(_pivot));
    }

    private static SnapCheckpoint CreateCheckpoint(BlockHeader anchor) => new()
    {
        AnchorNumber = anchor.Number,
        AnchorHash = anchor.Hash!,
        Partitions = [],
        StoragesToRetrieve = [],
        StorageRanges = [],
        AccountsToRefresh = [],
        CodesToRetrieve = [],
        UpdatedStorages = [],
    };

    private static void SeedBals(IContainer container, params BlockHeader[] headers)
    {
        IBlockAccessListStore balStore = container.Resolve<IBlockAccessListStore>();
        foreach (BlockHeader header in headers)
            balStore.Insert(header.Number, header.Hash!, new byte[] { 0xc0 });
    }
}
