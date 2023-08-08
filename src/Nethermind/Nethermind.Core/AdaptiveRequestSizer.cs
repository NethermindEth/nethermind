// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;

namespace Nethermind.Core;

/// <summary>
/// Encapsulate the pattern of adjusting request size depending on another metrics (usually the latency)
/// If the metric is lower that _lowerLatencyWatermark, the request size is increased,
/// if the metric is higher than _upperLatencyWatermark, the request size is decreased.
/// </summary>
public class AdaptiveRequestSizer
{
    private readonly int _minRequestLimit;
    private readonly int _maxRequestLimit;
    private readonly int _lowerWatermark;
    private readonly int _upperWatermark;
    private readonly double _adjustmentFactor;

    public int RequestSize { get; private set; }

    public AdaptiveRequestSizer(
        int minRequestLimit,
        int maxRequestLimit,
        int lowerWatermark,
        int upperWatermark,
        double adjustmentFactor = 2.0
    )
    {
        _maxRequestLimit = maxRequestLimit;
        _minRequestLimit = minRequestLimit;
        _lowerWatermark = lowerWatermark;
        _upperWatermark = upperWatermark;
        _adjustmentFactor = adjustmentFactor;

        RequestSize = _minRequestLimit;
    }

    /// <summary>
    /// Adjust the RequestSize depending on the latency of the request (in millis) and if the request failed.
    /// </summary>
    /// <param name="func"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task<T> MeasureLatency<T>(Func<int, Task<T>> func)
    {
        return await Measure(async (requestSize) =>
        {
            Stopwatch sw = Stopwatch.StartNew();
            T result = await func(requestSize);
            return (result, (int)sw.ElapsedMilliseconds);
        });
    }

    /// <summary>
    /// Adjust the RequestSize depending on the metrics of the request and if the request failed.
    /// </summary>
    /// <param name="func"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task<T> Measure<T>(Func<int, Task<(T, int)>> func)
    {
        // Record starting limit so that in case multiple concurrent request happens, we do not multiply the
        // limit on top of other adjustment, so only the last adjustment will stick, which is fine.
        int startingRequestSize = RequestSize;
        bool failed = false;
        int metric = 0;
        try
        {
            (T response, int m) = await func(startingRequestSize);
            metric = m;
            return response;
        }
        catch (Exception)
        {
            failed = true;
            throw;
        }
        finally
        {
            if (failed)
            {
                RequestSize = _minRequestLimit;
            }
            else if (metric < _lowerWatermark)
            {
                RequestSize = Math.Min((int)(startingRequestSize * _adjustmentFactor), _maxRequestLimit);
            }
            else if (metric > _upperWatermark && startingRequestSize > _minRequestLimit)
            {
                RequestSize = (int)(startingRequestSize / _adjustmentFactor);
            }
        }
    }
}
