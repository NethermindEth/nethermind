// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using NUnit.Framework;
using Nethermind.Core.RequestSizer;
using Nethermind.Core.Test.Threading;

namespace Nethermind.Core.Test.RequestSizer;

public class LatencyBasedRequestSizerTests
{
    /// <remarks>
    /// Latency is advanced on a <see cref="ManualTimeProvider"/> so the case lands on a known side of the
    /// 20ms/200ms watermarks regardless of machine load.
    /// </remarks>
    [TestCase(0, 3)]
    [TestCase(50, 2)]
    [TestCase(500, 1)]
    public async Task TestChangeInRequestSize(int latencyMs, int afterRequestSize)
    {
        ManualTimeProvider timeProvider = new();
        LatencyBasedRequestSizer sizer = new(
            1, 4,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(200),
            timeProvider: timeProvider);

        await sizer.MeasureLatency((_ => Task.FromResult(0)));
        await sizer.MeasureLatency(_ =>
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(latencyMs));
            return Task.FromResult(0);
        });

        int modifiedRequestSize = await sizer.MeasureLatency((Task.FromResult));

        Assert.That(modifiedRequestSize, Is.EqualTo(afterRequestSize));
    }
}
