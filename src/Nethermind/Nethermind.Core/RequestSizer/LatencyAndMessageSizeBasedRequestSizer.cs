// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Core.Test.Collections;

namespace Nethermind.Core;

/// <summary>
/// Encapsulate pattern of auto adjusting the request size depending on latency and response size.
/// Used for bodies and receipts where the response size affect memory usage also.
/// </summary>
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
        double adjustmentFactor = 1.5
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
    /// Adjust the request size depending on the latency and response size. Accept a list as request which will be capped.
    /// If the response size is too large, reduce request size.
    /// If the latency is above watermark, reduce request size.
    /// If the latency is below watermark and response size is not too large, increase request size.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="func"></param>
    /// <typeparam name="T">response type</typeparam>
    /// <typeparam name="TR">request type</typeparam>
    /// <returns></returns>
    public async Task<T> Run<T, TR>(IReadOnlyList<TR> request, Func<IReadOnlyList<TR>, Task<(T, long)>> func)
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
                && duration < _lowerLatencyWatermark)
            {
                return (result, AdaptiveRequestSizer.Direction.Increase);
            }

            return (result, AdaptiveRequestSizer.Direction.Stay);
        });
    }
}
