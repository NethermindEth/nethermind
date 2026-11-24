// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Merge.Plugin.BlockProduction;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.BlockProduction;

[TestFixture]
public class MergeBlockProducerRunnerTests
{
    [Test]
    public async Task StopAsync_stops_both_runners([Values] bool terminalBlockReached)
    {
        IBlockProducerRunner preMergeRunner = Substitute.For<IBlockProducerRunner>();
        IBlockProducerRunner postMergeRunner = Substitute.For<IBlockProducerRunner>();
        MergeBlockProducerRunner runner = CreateRunner(preMergeRunner, postMergeRunner, terminalBlockReached);

        runner.Start();
        await runner.StopAsync();

        await preMergeRunner.Received(1).StopAsync();
        await postMergeRunner.Received(1).StopAsync();
    }

    [Test]
    public async Task Disposing_the_merge_runner_releases_its_inner_runners_once()
    {
        IBlockProducerRunner preMerge = Substitute.For<IBlockProducerRunner, IDisposable>();
        IBlockProducerRunner postMerge = Substitute.For<IBlockProducerRunner, IAsyncDisposable>();
        MergeBlockProducerRunner runner = CreateRunner(preMerge, postMerge, terminalBlockReached: false);

        await runner.DisposeAsync();
        await runner.DisposeAsync();

        ((IDisposable)preMerge).Received(1).Dispose();
        await ((IAsyncDisposable)postMerge).Received(1).DisposeAsync();
    }

    private static MergeBlockProducerRunner CreateRunner(
        IBlockProducerRunner preMergeRunner,
        IBlockProducerRunner postMergeRunner,
        bool terminalBlockReached)
    {
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.HasEverReachedTerminalBlock().Returns(terminalBlockReached);
        return new MergeBlockProducerRunner(preMergeRunner, postMergeRunner, poSSwitcher);
    }
}
