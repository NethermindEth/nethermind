// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Core.Memory;
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
    private readonly long _lowMemoryMaxResponseSize;
    private readonly AdaptiveRequestSizer _requestSizer;
    private readonly IMemoryPressureHelper _memoryPressureHelper;

    public LatencyAndMessageSizeBasedRequestSizer(
        int minRequestLimit,
        int maxRequestLimit,
        TimeSpan lowerLatencyWatermark,
        TimeSpan upperLatencyWatermark,
        long maxResponseSize,
        long lowMemoryMaxResponseSize,
        int? initialRequestSize,
        double adjustmentFactor = 1.5,
        IMemoryPressureHelper? memoryPressureHelper = null
    )
    {
        _upperLatencyWatermark = upperLatencyWatermark;
        _lowerLatencyWatermark = lowerLatencyWatermark;
        _maxResponseSize = maxResponseSize;
        _lowMemoryMaxResponseSize = lowMemoryMaxResponseSize;
        _memoryPressureHelper = memoryPressureHelper ?? MemoryPressureHelper.Instance;

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
    /// <typeparam name="TResponse">response type</typeparam>
    /// <typeparam name="TRequest">request type</typeparam>
    /// <returns></returns>
    public async Task<TResponse> Run<TResponse, TRequest>(IReadOnlyList<TRequest> request, Func<IReadOnlyList<TRequest>, Task<(TResponse, long)>> func)
    {
        return await _requestSizer.Run(async (adjustedRequestSize) =>
        {
            long startTime = Stopwatch.GetTimestamp();
            (TResponse result, long messageSize) = await func(request.Clamp(adjustedRequestSize));
            TimeSpan duration = Stopwatch.GetElapsedTime(startTime);
            if (messageSize > _maxResponseSize)
            {
                return (result, AdaptiveRequestSizer.Direction.Decrease);
            }

            if (_memoryPressureHelper.GetCurrentMemoryPressure() == IMemoryPressureHelper.MemoryPressure.High && messageSize > _lowMemoryMaxResponseSize)
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
