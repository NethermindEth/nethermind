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
        _healing.ApplyRange(TestItem.KeccakA, firstPivot, secondPivot, Arg.Any<CancellationToken>()).Returns(TestItem.KeccakB);
        _healing.ApplyRange(TestItem.KeccakB, secondPivot, lastPivot, Arg.Any<CancellationToken>()).Returns(lastPivot.StateRoot);

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
        _pivot.GetPivotHeader().Returns(roundPivot);
        _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(TestItem.KeccakA);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunBalHealing(firstPivot, cts.Token));

        _healing.DidNotReceive().ApplyRange(Arg.Any<Hash256>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>());
        _healing.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    [Test]
    public void Does_not_finalize_when_the_healed_root_does_not_match_the_pivot()
    {
        using IContainer container = BuildRunnerContainer();
        StateSyncRunner runner = (StateSyncRunner)container.Resolve<IStateSyncRunner>();
        IBlockTree blockTree = container.Resolve<IBlockTree>();

        BlockHeader firstPivot = blockTree.FindHeader(10)!;
        BlockHeader lastPivot = blockTree.FindHeader(11)!;
        SeedBals(container, lastPivot);

        _pivot.GetPivotHeader().Returns(lastPivot);
        _healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(TestItem.KeccakA);
        _healing.ApplyRange(TestItem.KeccakA, firstPivot, lastPivot, Arg.Any<CancellationToken>()).Returns(TestItem.KeccakF);

        Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunBalHealing(firstPivot, default));

        _healing.DidNotReceive().FinalizeSync(Arg.Any<BlockHeader>());
    }

    private IContainer BuildRunnerContainer()
    {
        _healing = Substitute.For<IBalHealing>();

        _pivot = Substitute.For<IStateSyncPivot>();
        _pivot.UpdatedStorages.Returns([]);
        _pivot.CanFinalize(Arg.Any<BlockHeader>()).Returns(true);

        return PrepareDownloader(new RemoteDbContext(_logManager), configureBuilder: builder => builder
            .AddSingleton<IBalHealing>(_healing)
            .AddSingleton<IStateSyncPivot>(_pivot));
    }

    private static void SeedBals(IContainer container, params BlockHeader[] headers)
    {
        IBlockAccessListStore balStore = container.Resolve<IBlockAccessListStore>();
        foreach (BlockHeader header in headers)
            balStore.Insert(header.Number, header.Hash!, new byte[] { 0xc0 });
    }
}
