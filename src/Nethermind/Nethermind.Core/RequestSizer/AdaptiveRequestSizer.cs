// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Core;

/// <summary>
/// Encapsulate the pattern of adjusting request size depending on returned direction.
/// Usually that depends on another metrics (usually the latency). This class just handle changing the request size.
/// If the direction is Increase, the request size is increased,
/// if the direction is Decrease, the request size is decreased.
/// </summary>
public class AdaptiveRequestSizer
{
    private readonly int _minRequestLimit;
    private readonly int _maxRequestLimit;
    private readonly double _adjustmentFactor;

    internal int RequestSize { get; set; }

    public AdaptiveRequestSizer(
        int minRequestLimit,
        int maxRequestLimit,
        int? initialRequestSize = null,
        double adjustmentFactor = 1.5
    )
    {
        _maxRequestLimit = maxRequestLimit;
        _minRequestLimit = minRequestLimit;
        _adjustmentFactor = adjustmentFactor;

        RequestSize = initialRequestSize ?? _minRequestLimit;
    }

    /// <summary>
    /// Adjust the RequestSize depending on the requested direction or if the request failed.
    /// </summary>
    /// <param name="func"></param>
    /// <typeparam name="TResponse"></typeparam>
    /// <returns></returns>
    public async Task<TResponse> Run<TResponse>(Func<int, Task<(TResponse, Direction)>> func)
    {
        // Record starting limit so that in case multiple concurrent request happens, we do not multiply the
        // limit on top of other adjustment, so only the last adjustment will stick, which is fine.
        int startingRequestSize = RequestSize;
        Direction dir = Direction.Decrease; // For when it throws
        try
        {
            (TResponse response, Direction d) = await func(startingRequestSize);
            dir = d;
            return response;
        }
        finally
        {
            if (dir == Direction.Increase && startingRequestSize < _maxRequestLimit)
            {
                RequestSize = Math.Min((int)(Math.Ceiling(startingRequestSize * _adjustmentFactor)), _maxRequestLimit);
            }
            else if (dir == Direction.Decrease && startingRequestSize > _minRequestLimit)
            {
                RequestSize = Math.Max((int)(startingRequestSize / _adjustmentFactor), _minRequestLimit);
            }
        }
    }

    public enum Direction
    {
        Decrease,
        Increase,
        Stay
    }
}
