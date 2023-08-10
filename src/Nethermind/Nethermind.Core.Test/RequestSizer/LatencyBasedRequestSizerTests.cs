// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Core.Test.RequestSizer;

public class LatencyBasedRequestSizerTests
{
    [TestCase(0, 4)]
    [TestCase(20, 2)]
    [TestCase(100, 1)]
    public async Task TestWait(int waitTimeMs, int afterRequestSize)
    {
        LatencyBasedRequestSizer sizer = new(
            1, 4,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(50));

        await sizer.MeasureLatency((_ => Task.FromResult(0)));
        await sizer.MeasureLatency((async _ =>
        {
            await Task.Delay(waitTimeMs);
            return Task.FromResult(0);
        }));

        int modifiedRequestSize = await sizer.MeasureLatency((Task.FromResult));

        modifiedRequestSize.Should().Be(afterRequestSize);
    }
}
