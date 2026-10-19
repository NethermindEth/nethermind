// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.JsonRpc.Test;

internal static class TimeoutTestHelper
{
    private const int TimeoutCancellationTokenPoolSize = 64;

    internal static TrackingCancellationTokenSource RentTrackingTimeoutSourceForNextRequest()
    {
        foreach (CancellationTokenSource drained in DrainPool())
        {
            drained.Dispose();
        }

        TrackingCancellationTokenSource timeout = new();
        JsonRpcConfigExtension.ReturnTimeoutCancellationToken(timeout);
        return timeout;
    }

    /// <summary>Empties the timeout pool, disposing what it held, and reports whether <paramref name="timeout"/> was in it.</summary>
    /// <remarks>Other tests return sources to the shared pool at any time, so the planted source need not be the next one rented.</remarks>
    internal static bool DisposeIfPooled(TrackingCancellationTokenSource timeout)
    {
        bool pooled = false;
        foreach (CancellationTokenSource drained in DrainPool())
        {
            pooled |= ReferenceEquals(drained, timeout);
            drained.Dispose();
        }

        return pooled;
    }

    internal static void DisposeIfNotAlreadyObserved(TrackingCancellationTokenSource timeout)
    {
        if (timeout.DisposeCount == 0)
        {
            // Not pooled means a concurrent test rented it, and disposing it would fail that renter, so it is left to the GC.
            DisposeIfPooled(timeout);
        }
    }

    private static CancellationTokenSource[] DrainPool()
    {
        JsonRpcConfig config = new();
        CancellationTokenSource[] drained = new CancellationTokenSource[TimeoutCancellationTokenPoolSize];
        for (int i = 0; i < drained.Length; i++)
        {
            drained[i] = config.BuildTimeoutCancellationToken();
        }

        return drained;
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
