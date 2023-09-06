// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Core.Memory;

namespace Nethermind.Core;

public class LatencyBasedRequestSizer
{
    private readonly TimeSpan _upperWatermark;
    private readonly TimeSpan _lowerWatermark;
    private readonly AdaptiveRequestSizer _requestSizer;
    private readonly int _lowMemoryRequestLimit;
    private readonly IMemoryPressureHelper _memoryPressureHelper;

    public LatencyBasedRequestSizer(
        int minRequestLimit,
        int lowMemoryRequestLimit,
        int maxRequestLimit,
        TimeSpan lowerWatermark,
        TimeSpan upperWatermark,
        double adjustmentFactor = 1.5,
        int? initialRequestSize = null,
        IMemoryPressureHelper? memoryPressureHelper = null
    )
    {
        _upperWatermark = upperWatermark;
        _lowerWatermark = lowerWatermark;
        _lowMemoryRequestLimit = lowMemoryRequestLimit;
        _memoryPressureHelper = memoryPressureHelper ?? MemoryPressureHelper.Instance;

        _requestSizer = new AdaptiveRequestSizer(
            minRequestLimit,
            maxRequestLimit,
            adjustmentFactor: adjustmentFactor,
            initialRequestSize: initialRequestSize);
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

            if (_memoryPressureHelper.GetCurrentMemoryPressure() == IMemoryPressureHelper.MemoryPressure.High &&
                requestSize > _lowMemoryRequestLimit)
            {
                return (result, AdaptiveRequestSizer.Direction.Decrease);
            }

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
