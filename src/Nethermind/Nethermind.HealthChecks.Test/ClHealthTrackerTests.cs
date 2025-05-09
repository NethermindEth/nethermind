// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.HealthChecks.Test;

public class ClHealthTrackerTests
{
    [Test]
    public async Task ClHealthRequestsTracker_multiple_requests()
    {
        ManualTimestamper timestamper = new(DateTime.Parse("18:23:00"));
        await using ClHealthRequestsTracker healthTracker = new(timestamper, 300, NullLogger.Instance);

        healthTracker.CheckClAlive().Should().BeTrue();

        timestamper.Add(TimeSpan.FromSeconds(299));
        healthTracker.CheckClAlive().Should().BeTrue(); // Not enough time from start

        timestamper.Add(TimeSpan.FromSeconds(2));
        healthTracker.CheckClAlive().Should().BeFalse(); // More than 300 since start

        healthTracker.OnForkchoiceUpdatedCalled();
        healthTracker.CheckClAlive().Should().BeFalse(); // Only Fcu is not enough

        healthTracker.OnNewPayloadCalled();
        healthTracker.CheckClAlive().Should().BeTrue(); // Fcu + Np

        timestamper.Add(TimeSpan.FromSeconds(301));
        healthTracker.CheckClAlive().Should().BeFalse(); // Big gap since fcu + Np

        healthTracker.OnForkchoiceUpdatedCalled();
        healthTracker.CheckClAlive().Should().BeFalse(); // Only Fcu is not enough

        timestamper.Add(TimeSpan.FromSeconds(301));
        healthTracker.CheckClAlive().Should().BeFalse(); // Big gap

        healthTracker.OnNewPayloadCalled();
        healthTracker.CheckClAlive().Should().BeFalse(); // Previous fcu is too old

        timestamper.Add(TimeSpan.FromSeconds(299));
        healthTracker.OnForkchoiceUpdatedCalled();
        healthTracker.CheckClAlive().Should().BeTrue(); // New fcu close to Np expiry time

        timestamper.Add(TimeSpan.FromSeconds(2)); // Previous Np expired
        healthTracker.CheckClAlive().Should().BeFalse();
    }
}
