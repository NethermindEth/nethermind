// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Nethermind.Core.RequestSizer;
using Nethermind.Core.Test.Threading;

namespace Nethermind.Core.Test.RequestSizer;

public class LatencyAndMessageSizeBasedRequestSizerTests
{
    private static readonly int[] _sampleRequest = Enumerable.Range(0, 10).ToArray();

    /// <remarks>
    /// The sizer compares measured latency against its watermarks, so the request latency is advanced on a
    /// <see cref="ManualTimeProvider"/> rather than awaited. Sleeping for it made the outcome depend on how
    /// loaded the machine was: a <c>Task.Delay(50)</c> that overshot the 200ms upper watermark shrank the
    /// request instead of keeping it, and one that undershot the 20ms lower watermark grew it.
    /// </remarks>
    [TestCase(0, 0, 2, 3)]
    [TestCase(0, 10000, 2, 1)]
    [TestCase(50, 0, 2, 2)]
    [TestCase(50, 10000, 2, 1)]
    [TestCase(500, 0, 2, 1)]
    [TestCase(50, 0, 1, 1)]
    public async Task TestChangeInRequestSize(int latencyMs, long responseSize, int responseCount, int afterRequestSize)
    {
        ManualTimeProvider timeProvider = new();
        LatencyAndMessageSizeBasedRequestSizer sizer = new(
            1, 4,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(200),
            1000,
            2,
            timeProvider: timeProvider
        );

        await sizer.Run<int[], int, int>(_sampleRequest, adjustedSize =>
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(latencyMs));
            return Task.FromResult((new int[responseCount], responseSize));
        });

        IReadOnlyList<int> modifiedRequestSize = await sizer.Run<IReadOnlyList<int>, int, int>(
            _sampleRequest, (cappedRequest) => Task.FromResult((cappedRequest, (long)0)));

        Assert.That(modifiedRequestSize.Count, Is.EqualTo(afterRequestSize));
    }
}
