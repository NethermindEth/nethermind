// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.History;
using Nethermind.Init.Steps;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps;

[TestFixture]
public class StartHistoryPrunerTests
{
    [TestCase(PruningModes.Disabled, false, 0)]
    [TestCase(PruningModes.Disabled, true, 1)] // block access lists are still pruned
    [TestCase(PruningModes.Rolling, true, 1)]
    [TestCase(PruningModes.UseAncientBarriers, true, 1)]
    public async Task Execute_SchedulesPruningPassOnlyWhenEnabled(PruningModes pruning, bool prunesBlockAccessLists, int expectedPasses)
    {
        IHistoryPruner historyPruner = Substitute.For<IHistoryPruner>();
        historyPruner.BalCutoffBlockNumber.Returns(prunesBlockAccessLists ? 100UL : null);
        HistoryConfig historyConfig = new() { Pruning = pruning };

        await new StartHistoryPruner(historyPruner, historyConfig).Execute(CancellationToken.None);

        historyPruner.Received(expectedPasses).SchedulePruneHistory();
    }
}
