// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Nethermind.Core;

public class LatencyBasedRequestSizer
{
    private readonly TimeSpan _upperWatermark;
    private readonly TimeSpan _lowerWatermark;
    private readonly AdaptiveRequestSizer _requestSizer;

    public LatencyBasedRequestSizer(
        int minRequestLimit,
        int maxRequestLimit,
        TimeSpan lowerWatermark,
        TimeSpan upperWatermark,
        double adjustmentFactor = 2.0
    )
    {
        _upperWatermark = upperWatermark;
        _lowerWatermark = lowerWatermark;

        _requestSizer = new AdaptiveRequestSizer(minRequestLimit, maxRequestLimit, adjustmentFactor);
    }

    /// <summary>
    /// Adjust the RequestSize depending on the latency of the request (in millis) and if the request failed.
    /// </summary>
    /// <param name="func"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task<T> MeasureLatency<T>(Func<int, Task<T>> func)
    {
        return await _requestSizer.Run(async (requestSize) =>
        {
            Stopwatch sw = Stopwatch.StartNew();
            T result = await func(requestSize);
            TimeSpan duration = sw.Elapsed;
            if (duration < _lowerWatermark)
            {
                return (result, AdaptiveRequestSizer.Direction.Increase);
            }

            if (duration > _upperWatermark)
            {
                return (result, AdaptiveRequestSizer.Direction.Decrease);
            }

            return (result, AdaptiveRequestSizer.Direction.Stay);
        });
    }
}
