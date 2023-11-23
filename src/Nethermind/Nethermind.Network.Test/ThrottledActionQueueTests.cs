// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Logging;
using Nethermind.Network.P2P.ProtocolHandlers;
using NUnit.Framework;

namespace Nethermind.Network.Test;

public class ThrottledActionQueueTests
{
    [TestCase(100)]
    [TestCase(200)]
    [TestCase(500)]
    [TestCase(1000)]
    public void runs_single_action(int milliseconds)
    {
        bool ran = false;

        ThrottledActionQueue queue = new(TimeSpan.FromMilliseconds(milliseconds), LimboTraceLogger.Instance);
        queue.Init();
        queue.Enqueue(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        queue.Dispose();

        ran.Should().BeTrue();
    }

    [TestCase(100)]
    [TestCase(200)]
    [TestCase(500)]
    [TestCase(1000)]
    public void runs_single_task(int milliseconds)
    {
        bool ran = false;

        ThrottledActionQueue queue = new(TimeSpan.FromMilliseconds(milliseconds), LimboTraceLogger.Instance);
        queue.Init();
        queue.Enqueue(async () =>
        {
            await Task.Delay(200);
            ran = true;
        });
        queue.Dispose();

        ran.Should().BeTrue();
    }

    [TestCase(2, 0)]
    [TestCase(5, 100)]
    [TestCase(10, 500)]
    public void runs_action_throttled(int taskCount, int milliseconds)
    {
        List<DateTime> times = new();

        TimeSpan throttleTime = TimeSpan.FromMilliseconds(500);
        TimeSpan epsilon = throttleTime * 0.01; // Margin of error

        ThrottledActionQueue queue = new(throttleTime, LimboTraceLogger.Instance);
        queue.Init();
        for (int i = 0; i < taskCount; i++)
        {
            queue.Enqueue(() =>
            {
                times.Add(DateTime.UtcNow);
                return Task.CompletedTask;
            });
        }
        queue.Dispose();

        DateTime last = times[0];
        for (int i = 1; i < times.Count; i++)
        {
            DateTime current = times[i];

            TimeSpan delta = (times[i] - last) + epsilon;
            delta.Should().BeGreaterOrEqualTo(throttleTime);

            last = current;
        }
    }

    [TestCase(2, 0)]
    [TestCase(5, 100)]
    [TestCase(10, 500)]
    public void runs_tasks_throttled(int taskCount, int milliseconds)
    {
        List<DateTime> times = new();

        TimeSpan throttleTime = TimeSpan.FromMilliseconds(500);
        TimeSpan epsilon = throttleTime * 0.01; // Margin of error

        ThrottledActionQueue queue = new(throttleTime, LimboTraceLogger.Instance);
        queue.Init();
        for (int i = 0; i < taskCount; i++)
        {
            queue.Enqueue(async () =>
            {
                await Task.Delay(100);
                times.Add(DateTime.UtcNow);
            });
        }
        queue.Dispose();

        DateTime last = times[0];
        for (int i = 1; i < times.Count; i++)
        {
            DateTime current = times[i];

            TimeSpan delta = (times[i] - last) + epsilon;
            delta.Should().BeGreaterOrEqualTo(throttleTime);

            last = current;
        }
    }

    [Test]
    public void supports_multiple_disposes()
    {
        ThrottledActionQueue queue = new(TimeSpan.Zero, LimboTraceLogger.Instance);
        queue.Dispose();
        queue.Dispose();
    }

    [Test]
    public void supports_multiple_disposes_concurrent()
    {
        ThrottledActionQueue queue = new(TimeSpan.Zero, LimboTraceLogger.Instance);
        Parallel.For(0, 10, _ => queue.Dispose());
    }
}
