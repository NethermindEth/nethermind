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
        double adjustmentFactor = 1.5
    )
    {
        _upperWatermark = upperWatermark;
        _lowerWatermark = lowerWatermark;

        _requestSizer = new AdaptiveRequestSizer(minRequestLimit, maxRequestLimit, adjustmentFactor: adjustmentFactor);
    }

    /// <summary>
    /// Adjust the RequestSize depending on the latency of the request
    /// </summary>
    /// <param name="func"></param>
    /// <typeparam name="TResponse"></typeparam>
    /// <returns></returns>
    public async Task<TResponse> MeasureLatency<TResponse>(Func<int, Task<TResponse>> func)
    {
        return await _requestSizer.Run(async (requestSize) =>
        {
            long startTime = Stopwatch.GetTimestamp();
            TResponse result = await func(requestSize);
            TimeSpan duration = Stopwatch.GetElapsedTime(startTime);
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
