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
    [TestCase(PruningModes.Disabled, 0)]
    [TestCase(PruningModes.Rolling, 1)]
    [TestCase(PruningModes.UseAncientBarriers, 1)]
    public async Task Execute_SchedulesPruningPassOnlyWhenEnabled(PruningModes pruning, int expectedPasses)
    {
        IHistoryPruner historyPruner = Substitute.For<IHistoryPruner>();
        HistoryConfig historyConfig = new() { Pruning = pruning };

        await new StartHistoryPruner(historyPruner, historyConfig).Execute(CancellationToken.None);

        historyPruner.Received(expectedPasses).SchedulePruneHistory();
    }
}
