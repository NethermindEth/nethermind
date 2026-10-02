// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
