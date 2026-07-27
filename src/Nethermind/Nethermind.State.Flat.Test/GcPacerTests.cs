// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Autofac;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class GcPacerTests
{
    // A cadence long enough that the started daemon threads never induce a collection during the run.
    private const long IdleIntervalMs = 600_000;

    [TestCase(0L, 0L, false, TestName = "TryStart_PacingDisabled_DoesNotStart")]
    [TestCase(IdleIntervalMs, 0L, true, TestName = "TryStart_Gen1CadenceOnly_Starts")]
    [TestCase(0L, IdleIntervalMs, true, TestName = "TryStart_Gen0CadenceOnly_Starts")]
    public void TryStart_FollowsConfiguredCadence(long gen1IntervalMs, long gen0IntervalMs, bool expectedStarted)
    {
        GcPacer pacer = new(
            new FlatDbConfig { GcPaceIntervalMs = gen1IntervalMs, GcPaceGen0IntervalMs = gen0IntervalMs },
            LimboLogs.Instance);

        Assert.That(pacer.TryStart(), Is.EqualTo(expectedStarted));
    }

    [Test]
    public void TryStart_SecondCall_IsNoOp()
    {
        GcPacer pacer = new(new FlatDbConfig { GcPaceIntervalMs = IdleIntervalMs }, LimboLogs.Instance);

        pacer.TryStart();

        Assert.That(pacer.TryStart(), Is.False);
    }

    [Test]
    public void Module_StartsPacerFromContainer()
    {
        using FlatTestContainer container = new();

        Assert.That(container.Resolve<IEnumerable<IStartable>>(), Has.Some.InstanceOf<GcPacer>());
    }
}
