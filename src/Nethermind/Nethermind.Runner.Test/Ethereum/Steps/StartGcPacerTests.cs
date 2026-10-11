// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core.Memory;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps;

public class StartGcPacerTests
{
    [Test]
    public async Task Execute_StartsPacer()
    {
        const long idleIntervalMs = 600_000;
        InitConfig initConfig = new() { GcPaceGen1IntervalMs = idleIntervalMs };
        using GcPacer pacer = new(0, idleIntervalMs, 0, 0, LimboLogs.Instance);

        await new StartGcPacer(pacer, initConfig, LimboLogs.Instance).Execute(CancellationToken.None);

        Assert.That(pacer.TryStart(), Is.False);
    }
}
