// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using System;
using System.Diagnostics;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace Nethermind.Network.P2P;

public class RateLimiter : IRateLimiter
{
    private readonly SlidingWindowRateLimiter _rateLimiter;

    static RateLimiter()
    {
        int bytesPerSecond = (int)500.KB();
        int window = 5; //seconds
        Instance = new RateLimiter(new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            QueueProcessingOrder = QueueProcessingOrder.NewestFirst,
            PermitLimit = bytesPerSecond * window,
            QueueLimit = bytesPerSecond * window / 2,
            Window = TimeSpan.FromSeconds(window),
            SegmentsPerWindow = window * 10,
            AutoReplenishment = true,
        }));
    }

    public RateLimiter(SlidingWindowRateLimiter rateLimiter)
    {
        _rateLimiter = rateLimiter;
    }

    public static IRateLimiter Instance { get; }

    public async Task<bool> ThrottleAsync(int packetType, int length, ILogger logger)
    {
        switch (packetType)
        {
            case Eth65MessageCode.PooledTransactions:
            case Eth65MessageCode.NewPooledTransactionHashes:
            case Eth65MessageCode.GetPooledTransactions:
                var sw = Stopwatch.StartNew();
                var ack = await _rateLimiter.AcquireAsync(length);
                sw.Stop();
                logger.Warn($"Throttling {packetType} with length {length} with result {ack.IsAcquired}, spent {sw.ElapsedMilliseconds}");
                return ack.IsAcquired;
            default:
                return true;
        }
    }
}

public interface IRateLimiter
{
    /// <summary>
    /// Throttle by delaying or even dropping(wen result is false)
    /// </summary>
    /// <param name="packetType"></param>
    /// <param name="length"></param>
    /// <returns></returns>
    Task<bool> ThrottleAsync(int packetType, int length, ILogger logger);
}
