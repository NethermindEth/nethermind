// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Memory;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.RequestSizer;

public class LatencyAndMessageSizeBasedRequestSizerTests
{
    private static readonly int[] _sampleRequest = Enumerable.Range(0, 10).ToArray();

    [TestCase(0, 0, 3, false)]
    [TestCase(0, 10000, 1, false)]
    [TestCase(0, 200, 3, false)]
    [TestCase(0, 200, 1, true)]
    [TestCase(20, 0, 2, false)]
    [TestCase(20, 10000, 1, false)]
    [TestCase(100, 0, 1, false)]
    public async Task TestChangeInRequestSize(int waitTimeMs, long responseSize, int afterRequestSize, bool underHighMemoryPressure)
    {
        IMemoryPressureHelper memoryPressureHelper = Substitute.For<IMemoryPressureHelper>();
        memoryPressureHelper.GetCurrentMemoryPressure().Returns(underHighMemoryPressure
            ? IMemoryPressureHelper.MemoryPressure.High
            : IMemoryPressureHelper.MemoryPressure.Low);


        LatencyAndMessageSizeBasedRequestSizer sizer = new(
            1, 4,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(50),
            1000,
            100,
            2,
            memoryPressureHelper: memoryPressureHelper
        );

        await sizer.Run(_sampleRequest, async _ =>
        {
            await Task.Delay(waitTimeMs);
            return (0, responseSize);
        });

        int modifiedRequestSize = await sizer.Run(_sampleRequest, (cappedRequest) => Task.FromResult((cappedRequest.Count, (long)0)));

        modifiedRequestSize.Should().Be(afterRequestSize);
    }
}
