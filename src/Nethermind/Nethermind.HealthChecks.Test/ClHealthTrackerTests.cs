// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.HealthChecks.Test;

public class ClHealthTrackerTests
{
    private const int MaxIntervalSeconds = 300;

    private static ClHealthRequestsTracker CreateHealthTracker(ManualTimestamper timestamper)
    {
        return new ClHealthRequestsTracker(
            timestamper,
            new HealthChecksConfig()
            {
                MaxIntervalClRequestTime = MaxIntervalSeconds
            }, LimboLogs.Instance);
    }

    [Test]
    public void CheckClAlive_Initially_ReturnsTrue()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        healthTracker.CheckClAlive().Should().BeTrue();
    }

    [Test]
    public void CheckClAlive_AfterMaxInterval_ReturnsFalse()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds + 1));
        healthTracker.CheckClAlive().Should().BeFalse();
    }

    [Test]
    public void CheckClAlive_BeforeMaxInterval_ReturnsTrue()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds - 1));
        healthTracker.CheckClAlive().Should().BeTrue();
    }

    [Test]
    public void CheckClAlive_AfterForkchoiceUpdated_ResetsTimer()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds + 1));
        healthTracker.CheckClAlive().Should().BeFalse();

        healthTracker.OnForkchoiceUpdatedCalled();
        healthTracker.CheckClAlive().Should().BeTrue();
    }

    [Test]
    public void CheckClAlive_AfterNewPayload_ResetsTimer()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds + 1));
        healthTracker.CheckClAlive().Should().BeFalse();

        healthTracker.OnNewPayloadCalled();
        healthTracker.CheckClAlive().Should().BeTrue();
    }

    [Test]
    public void CheckClAlive_WithBothRequests_WorksCorrectly()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        healthTracker.OnForkchoiceUpdatedCalled();
        healthTracker.CheckClAlive().Should().BeTrue();

        healthTracker.OnNewPayloadCalled();
        healthTracker.CheckClAlive().Should().BeTrue();

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds + 1));
        healthTracker.CheckClAlive().Should().BeFalse();
    }

    [Test]
    public void CheckClAlive_AtBoundaryConditions_WorksCorrectly()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        ClHealthRequestsTracker healthTracker = CreateHealthTracker(timestamper);

        timestamper.Add(TimeSpan.FromSeconds(MaxIntervalSeconds - 1));
        healthTracker.OnForkchoiceUpdatedCalled();
        healthTracker.CheckClAlive().Should().BeTrue();

        timestamper.Add(TimeSpan.FromSeconds(2));
        healthTracker.CheckClAlive().Should().BeTrue();
    }
}
