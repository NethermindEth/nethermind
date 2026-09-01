// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
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
    public void Keeps_retrying_while_no_peer_serves_the_bals()
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

        Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunBalHealing(firstPivot, cts.Token));

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

    [TestCase(false, TestName = "Does_not_finalize_when_the_healed_root_does_not_match_the_pivot")]
    [TestCase(true, TestName = "Does_not_finalize_when_the_range_is_lost")]
    public void Does_not_finalize_when_healing_cannot_reach_the_pivot(bool rangeLost)
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

        Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunBalHealing(firstPivot, default));

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

        await runner.RunSnapSyncWithBalHealing(orphaned, default);

        // The first attempt healed onto an orphaned pivot, so it must be thrown away and synced again.
        await _snapSyncRunner.Received(2).Run(Arg.Any<CancellationToken>());
        _healing.Received(1).FinalizeSync(canonical);
    }

    private IContainer BuildRunnerContainer()
    {
        _healing = Substitute.For<IBalHealing>();
        _snapSyncRunner = Substitute.For<ISnapSyncRunner>();

        _pivot = Substitute.For<IStateSyncPivot>();
        _pivot.UpdatedStorages.Returns([]);
        _pivot.CanFinalize(Arg.Any<BlockHeader>()).Returns(true);

        // No peer here can serve BALs, so waiting on allocation only slows the retry test down.
        return PrepareDownloader(new RemoteDbContext(_logManager), syncDispatcherAllocateTimeoutMs: 0, configureBuilder: builder => builder
            .AddSingleton<IBalHealing>(_healing)
            .AddSingleton<ISnapSyncRunner>(_snapSyncRunner)
            .AddSingleton<IStateSyncPivot>(_pivot));
    }

    private static void SeedBals(IContainer container, params BlockHeader[] headers)
    {
        IBlockAccessListStore balStore = container.Resolve<IBlockAccessListStore>();
        foreach (BlockHeader header in headers)
            balStore.Insert(header.Number, header.Hash!, new byte[] { 0xc0 });
    }
}
