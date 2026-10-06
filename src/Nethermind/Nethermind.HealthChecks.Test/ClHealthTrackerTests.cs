// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.HealthChecks.Test;

public class ClHealthTrackerTests
{
    private const int MaxIntervalSeconds = 300;

    private static ClHealthRequestsTracker CreateHealthTracker(ManualTimestamper timestamper, ILogManager logManager = null) => new(
            timestamper,
            new HealthChecksConfig()
            {
                MaxIntervalClRequestTime = MaxIntervalSeconds
            }, logManager ?? LimboLogs.Instance);

    [Test]
    public void CheckClAlive_Initially_ReturnsTrue()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        Assert.That(healthTracker.CheckClAlive(), Is.True);
    }

    [Test]
    public void CheckClAlive_AfterMaxInterval_ReturnsFalse()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds + 1));
        Assert.That(healthTracker.CheckClAlive(), Is.False);
    }

    [Test]
    public void CheckClAlive_BeforeMaxInterval_ReturnsTrue()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds - 1));
        Assert.That(healthTracker.CheckClAlive(), Is.True);
    }

    [Test]
    public void CheckClAlive_AfterForkchoiceUpdated_ResetsTimer()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds + 1));
        Assert.That(healthTracker.CheckClAlive(), Is.False);

        healthTracker.OnForkchoiceUpdatedCalled();
        Assert.That(healthTracker.CheckClAlive(), Is.True);
    }

    [Test]
    public void CheckClAlive_AfterNewPayload_ResetsTimer()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds + 1));
        Assert.That(healthTracker.CheckClAlive(), Is.False);

        healthTracker.OnNewPayloadCalled();
        Assert.That(healthTracker.CheckClAlive(), Is.True);
    }

    [Test]
    public void CheckClAlive_WithBothRequests_WorksCorrectly()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        healthTracker.OnForkchoiceUpdatedCalled();
        Assert.That(healthTracker.CheckClAlive(), Is.True);

        healthTracker.OnNewPayloadCalled();
        Assert.That(healthTracker.CheckClAlive(), Is.True);

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds + 1));
        Assert.That(healthTracker.CheckClAlive(), Is.False);
    }

    [Test]
    public void CheckClAlive_AtBoundaryConditions_WorksCorrectly()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds - 1));
        healthTracker.OnForkchoiceUpdatedCalled();
        Assert.That(healthTracker.CheckClAlive(), Is.True);

        timestamper.Add(TimeSpan.FromSeconds(2));
        Assert.That(healthTracker.CheckClAlive(), Is.True);
    }

    [Test]
    public void ReportClStatus_WarnsWithDocsLink_OnlyWhenClSilent([Values] bool clSilent)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper, new OneLoggerLogManager(new ILogger(logger)));

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds + 1));
        if (!clSilent) healthTracker.OnForkchoiceUpdatedCalled();
        healthTracker.ReportClStatus(null);

        logger.Received(clSilent ? 1 : 0).Warn(Arg.Is<string>(m => m.Contains("https://docs.nethermind.io/get-started/running-node/consensus-clients/")));
    }
}
