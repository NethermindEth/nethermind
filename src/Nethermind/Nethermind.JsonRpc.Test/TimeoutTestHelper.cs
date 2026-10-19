// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;

namespace Nethermind.JsonRpc.Test;

internal static class TimeoutTestHelper
{
    private const int TimeoutCancellationTokenPoolSize = 64;

    internal static TrackingCancellationTokenSource RentTrackingTimeoutSourceForNextRequest()
    {
        JsonRpcConfig config = new();
        List<CancellationTokenSource> rentedTimeouts = new(TimeoutCancellationTokenPoolSize);
        for (int i = 0; i < TimeoutCancellationTokenPoolSize; i++)
        {
            rentedTimeouts.Add(config.BuildTimeoutCancellationToken());
        }

        for (int i = 0; i < rentedTimeouts.Count; i++)
        {
            rentedTimeouts[i].Dispose();
        }

        TrackingCancellationTokenSource timeout = new();
        JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeout);
        return timeout;
    }

    /// <summary>Removes <paramref name="timeout"/> from the shared pool, wherever other tests' returns have queued it.</summary>
    /// <returns>Whether <paramref name="timeout"/> was in the pool.</returns>
    internal static bool TryTakeFromPool(CancellationTokenSource timeout)
    {
        JsonRpcConfig config = new();
        List<CancellationTokenSource> others = new(TimeoutCancellationTokenPoolSize);
        bool found = false;
        for (int i = 0; i < TimeoutCancellationTokenPoolSize && !found; i++)
        {
            CancellationTokenSource rented = config.BuildTimeoutCancellationToken();
            if (ReferenceEquals(rented, timeout)) found = true;
            else others.Add(rented);
        }

        for (int i = 0; i < others.Count; i++)
        {
            JsonRpcConfigExtension.ReturnTimeoutCancellationToken(others[i]);
        }

        return found;
    }

    internal static void DisposeIfNotAlreadyObserved(TrackingCancellationTokenSource timeout)
    {
        if (timeout.DisposeCount == 0)
        {
            // A disposed source left in the pool would fail the next rent anywhere in the process.
            TryTakeFromPool(timeout);
            timeout.Dispose();
        }
    }

    internal sealed class TrackingCancellationTokenSource : CancellationTokenSource
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref _disposeCount);
            }

            base.Dispose(disposing);
        }
    }
}
