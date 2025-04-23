// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Core.Collections;

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
    public int RequestSize => _requestSizer.RequestSize;

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
    /// If the response size (byte) is too large, reduce request size.
    /// If the response size (count) less than request size, reduce request size.
    /// If the latency is above watermark, reduce request size.
    /// If the latency is below watermark and response size is not too large, increase request size.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="func"></param>
    /// <typeparam name="TResponse">response type</typeparam>
    /// <typeparam name="TRequest">request type</typeparam>
    /// <typeparam name="TResponseItem">response item type</typeparam>
    /// <returns></returns>
    public async Task<TResponse> Run<TResponse, TRequest, TResponseItem>(IReadOnlyList<TRequest> request, Func<IReadOnlyList<TRequest>, Task<(TResponse, long)>> func) where TResponse : IReadOnlyList<TResponseItem>
    {
        return await _requestSizer.Run(async (adjustedRequestSize) =>
        {
            long startTime = Stopwatch.GetTimestamp();
            long affectiveRequestSize = Math.Min(adjustedRequestSize, request.Count);
            (TResponse result, long messageSize) = await func(request.Slice(0, Math.Min(adjustedRequestSize, request.Count)));
            TimeSpan duration = Stopwatch.GetElapsedTime(startTime);
            if (messageSize > _maxResponseSize)
            {
                return (result, AdaptiveRequestSizer.Direction.Decrease);
            }

            if (duration > _upperLatencyWatermark)
            {
                return (result, AdaptiveRequestSizer.Direction.Decrease);
            }

            if (result.Count < affectiveRequestSize)
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
