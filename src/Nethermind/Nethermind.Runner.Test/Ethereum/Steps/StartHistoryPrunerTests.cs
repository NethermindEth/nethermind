// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.History;
using Nethermind.Init.Steps;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps;

[TestFixture]
public class StartHistoryPrunerTests
{
    [TestCase(PruningModes.Disabled, false, 0)]
    [TestCase(PruningModes.Disabled, true, 2)] // block access lists are still pruned
    [TestCase(PruningModes.Rolling, true, 2)] // the startup pass plus one for the emptied queue
    [TestCase(PruningModes.UseAncientBarriers, true, 2)]
    public async Task Execute_SchedulesPruningPassOnlyWhenEnabled(PruningModes pruning, bool prunesBlockAccessLists, int expectedPasses)
    {
        IHistoryPruner historyPruner = Substitute.For<IHistoryPruner>();
        historyPruner.BalCutoffBlockNumber.Returns(prunesBlockAccessLists ? 100UL : null);
        HistoryConfig historyConfig = new() { Pruning = pruning };

        IBlockProcessingQueue blockProcessingQueue = Substitute.For<IBlockProcessingQueue>();

        await new StartHistoryPruner(historyPruner, historyConfig, blockProcessingQueue).Execute(CancellationToken.None);
        blockProcessingQueue.ProcessingQueueEmpty += Raise.Event();

        historyPruner.Received(expectedPasses).SchedulePruneHistory();
    }
}
