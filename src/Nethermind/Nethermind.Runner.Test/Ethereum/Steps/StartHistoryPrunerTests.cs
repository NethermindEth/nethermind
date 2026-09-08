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
    [Test]
    public async Task Execute_SchedulesPruningPass()
    {
        IHistoryPruner historyPruner = Substitute.For<IHistoryPruner>();

        await new StartHistoryPruner(historyPruner).Execute(CancellationToken.None);

        historyPruner.Received(1).SchedulePruneHistory();
    }
}
