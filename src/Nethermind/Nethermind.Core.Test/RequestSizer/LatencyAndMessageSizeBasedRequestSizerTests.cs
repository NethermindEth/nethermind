// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Core.Test.RequestSizer;

public class LatencyAndMessageSizeBasedRequestSizerTests
{
    private static readonly int[] _sampleRequest = Enumerable.Range(0, 10).ToArray();

    [TestCase(0, 0, 2, 3)]
    [TestCase(0, 10000, 2, 1)]
    [TestCase(50, 0, 2, 2)]
    [TestCase(50, 10000, 2, 1)]
    [TestCase(500, 0, 2, 1)]
    [TestCase(50, 0, 1, 1)]
    public async Task TestChangeInRequestSize(int waitTimeMs, long responseSize, int responseCount, int afterRequestSize)
    {
        LatencyAndMessageSizeBasedRequestSizer sizer = new(
            1, 4,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(200),
            1000,
            2
        );

        await sizer.Run<int[], int, int>(_sampleRequest, async adjustedSize =>
        {
            await Task.Delay(waitTimeMs);
            return (new int[responseCount], responseSize);
        });

        IReadOnlyList<int> modifiedRequestSize = await sizer.Run<IReadOnlyList<int>, int, int>(
            _sampleRequest, (cappedRequest) => Task.FromResult((cappedRequest, (long)0)));

        modifiedRequestSize.Count.Should().Be(afterRequestSize);
    }
}
