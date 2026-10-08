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
    [TestCase(PruningModes.Disabled, 0)]
    [TestCase(PruningModes.Rolling, 2)] // the startup pass plus one for the emptied queue
    [TestCase(PruningModes.UseAncientBarriers, 2)]
    public async Task Execute_SchedulesPruningPassOnlyWhenEnabled(PruningModes pruning, int expectedPasses)
    {
        IHistoryPruner historyPruner = Substitute.For<IHistoryPruner>();
        HistoryConfig historyConfig = new() { Pruning = pruning };

        IBlockProcessingQueue blockProcessingQueue = Substitute.For<IBlockProcessingQueue>();

        await new StartHistoryPruner(historyPruner, historyConfig, blockProcessingQueue).Execute(CancellationToken.None);
        blockProcessingQueue.ProcessingQueueEmpty += Raise.Event();

        historyPruner.Received(expectedPasses).SchedulePruneHistory();
    }
}
