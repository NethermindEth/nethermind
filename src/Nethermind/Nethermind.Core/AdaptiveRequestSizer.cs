// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Nethermind.Core;

/// <summary>
/// Encapsulate the pattern of adjusting request size depending on the latency of the request.
/// If the latency is lower that _lowerLatencyWatermark, the request size is increased,
/// if the latency is higher than _upperLatencyWatermark, the request size is decreased.
/// </summary>
public class AdaptiveRequestSizer
{
    private readonly int _minRequestLimit;
    private readonly int _maxRequestLimit;
    private readonly TimeSpan _lowerLatencyWatermark;
    private readonly TimeSpan _upperLatencyWatermark;
    private readonly double _adjustmentFactor;

    public int RequestSize { get; private set; }

    public AdaptiveRequestSizer(
        int minRequestLimit,
        int maxRequestLimit,
        TimeSpan lowerLatencyWatermark,
        TimeSpan upperLatencyWatermark,
        double adjustmentFactor = 2.0
    )
    {
        _maxRequestLimit = maxRequestLimit;
        _minRequestLimit = minRequestLimit;
        _lowerLatencyWatermark = lowerLatencyWatermark;
        _upperLatencyWatermark = upperLatencyWatermark;
        _adjustmentFactor = adjustmentFactor;

        RequestSize = _minRequestLimit;
    }

    /// <summary>
    /// Adjust the RequestSize depending on the latency of the request and if the request failed.
    /// </summary>
    /// <param name="func"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public async Task<T> MeasureLatency<T>(Func<int, Task<T>> func)
    {
        // Record bytes limit so that in case multiple concurrent request happens, we do not multiply the
        // limit on top of other adjustment, so only the last adjustment will stick, which is fine.
        int startingRequestSize = RequestSize;
        bool failed = false;
        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            return await func(startingRequestSize);
        }
        catch (Exception)
        {
            failed = true;
            throw;
        }
        finally
        {
            sw.Stop();
            if (failed)
            {
                RequestSize = _minRequestLimit;
            }
            else if (sw.Elapsed < _lowerLatencyWatermark)
            {
                RequestSize = Math.Min((int)(startingRequestSize * _adjustmentFactor), _maxRequestLimit);
            }
            else if (sw.Elapsed > _upperLatencyWatermark && startingRequestSize > _minRequestLimit)
            {
                RequestSize = (int)(startingRequestSize / _adjustmentFactor);
            }
        }
    }
}
