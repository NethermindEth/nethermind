// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Memory;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.RequestSizer;

public class LatencyBasedRequestSizerTests
{
    [TestCase(0, 2, 3, false)]
    [TestCase(20, 2, 2, false)]
    [TestCase(100, 2, 1, false)]
    [TestCase(0, 3, 2, true)]
    public async Task TestWait(int waitTimeMs, int initialRequestSize, int afterRequestSize, bool underHighMemoryPressure)
    {
        IMemoryPressureHelper memoryPressureHelper = Substitute.For<IMemoryPressureHelper>();
        memoryPressureHelper.GetCurrentMemoryPressure().Returns(underHighMemoryPressure
            ? IMemoryPressureHelper.MemoryPressure.High
            : IMemoryPressureHelper.MemoryPressure.Low);

        LatencyBasedRequestSizer sizer = new(
            1, 2, 4,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(50),
            initialRequestSize: initialRequestSize,
            memoryPressureHelper: memoryPressureHelper);

        await sizer.MeasureLatency((async _ =>
        {
            await Task.Delay(waitTimeMs);
            return Task.FromResult(0);
        }));

        int modifiedRequestSize = await sizer.MeasureLatency((Task.FromResult));

        modifiedRequestSize.Should().Be(afterRequestSize);
    }
}
