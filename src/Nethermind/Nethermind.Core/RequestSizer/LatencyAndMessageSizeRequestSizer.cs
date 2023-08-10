// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Core.Test.Collections;

namespace Nethermind.Core;

public class LatencyAndMessageSizeBasedRequestSizer
{
    private readonly TimeSpan _upperLatencyWatermark;
    private readonly TimeSpan _lowerLatencyWatermark;
    private readonly long _maxResponseSize;
    private readonly AdaptiveRequestSizer _requestSizer;

    public LatencyAndMessageSizeBasedRequestSizer(
        int minRequestLimit,
        int maxRequestLimit,
        TimeSpan lowerLatencyWatermark,
        TimeSpan upperLatencyWatermark,
        long maxResponseSize,
        int? initialRequestSize,
        double adjustmentFactor = 2.0
    )
    {
        _upperLatencyWatermark = upperLatencyWatermark;
        _lowerLatencyWatermark = lowerLatencyWatermark;
        _maxResponseSize = maxResponseSize;

        _requestSizer = new AdaptiveRequestSizer(
            minRequestLimit,
            maxRequestLimit,
            initialRequestSize: initialRequestSize,
            adjustmentFactor: adjustmentFactor);
    }

    /// <summary>
    /// Adjust the RequestSize depending on the latency of the request (in millis) and if the request failed.
    /// </summary>
    /// <param name="func"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task<T> Run<T, R>(IReadOnlyList<R> request, Func<IReadOnlyList<R>, Task<(T, long)>> func)
    {
        return await _requestSizer.Run(async (adjustedRequestSize) =>
        {
            Stopwatch sw = Stopwatch.StartNew();
            (T result, long messageSize) = await func(request.Clamp(adjustedRequestSize));
            TimeSpan duration = sw.Elapsed;
            if (messageSize > _maxResponseSize)
            {
                return (result, AdaptiveRequestSizer.Direction.Decrease);
            }

            if (duration > _upperLatencyWatermark)
            {
                return (result, AdaptiveRequestSizer.Direction.Decrease);
            }

            if (
                request.Count >= adjustedRequestSize // If the original request size is low, increasing wont do anything
                && duration < _lowerLatencyWatermark
                && messageSize < _maxResponseSize)
            {
                return (result, AdaptiveRequestSizer.Direction.Increase);
            }


            return (result, AdaptiveRequestSizer.Direction.Stay);
        });
    }
}
