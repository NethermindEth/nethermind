// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
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
        using GcPacer pacer = new(
            new FlatDbConfig { GcPaceIntervalMs = gen1IntervalMs, GcPaceGen0IntervalMs = gen0IntervalMs },
            LimboLogs.Instance);

        Assert.That(pacer.TryStart(), Is.EqualTo(expectedStarted));
    }

    [Test]
    public void TryStart_SecondCall_IsNoOp()
    {
        using GcPacer pacer = new(new FlatDbConfig { GcPaceIntervalMs = IdleIntervalMs }, LimboLogs.Instance);

        pacer.TryStart();

        Assert.That(pacer.TryStart(), Is.False);
    }

    [Test]
    public void Dispose_StopsPacerThreads()
    {
        GcPacer pacer = new(
            new FlatDbConfig { GcPaceIntervalMs = IdleIntervalMs, GcPaceGen0IntervalMs = IdleIntervalMs },
            LimboLogs.Instance);
        Assert.That(pacer.TryStart(), Is.True);

        // Dispose joins the pacer threads; it only returns if cancellation interrupts their idle wait.
        Task disposed = Task.Run(pacer.Dispose);

        Assert.That(disposed.Wait(TimeSpan.FromSeconds(10)), Is.True);
    }

    [Test]
    public void Dispose_IsIdempotent()
    {
        GcPacer pacer = new(new FlatDbConfig { GcPaceIntervalMs = IdleIntervalMs }, LimboLogs.Instance);
        pacer.TryStart();

        pacer.Dispose();

        Assert.DoesNotThrow(pacer.Dispose);
    }

    [Test]
    public void Module_RegistersPacerAsSingleton()
    {
        using FlatTestContainer container = new();

        GcPacer pacer = container.Resolve<GcPacer>();
        Assert.That(container.Resolve<GcPacer>(), Is.SameAs(pacer));
    }
}
