// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;

namespace Nethermind.JsonRpc.Test;

internal static class TimeoutTestHelper
{
    private const int TimeoutCancellationTokenPoolSize = 64;

    /// <summary>Creates the source the next timeout rent in the calling async flow will receive.</summary>
    /// <remarks>Empties the shared pool first, so that a request returning the source finds room for it.</remarks>
    internal static TrackingCancellationTokenSource RentTrackingTimeoutSourceForNextRequest()
    {
        JsonRpcConfig config = new();
        for (int i = 0; i < TimeoutCancellationTokenPoolSize; i++)
        {
            config.BuildTimeoutCancellationToken().Dispose();
        }

        TrackingCancellationTokenSource timeout = new();
        JsonRpcConfigExtension.UseForNextTimeoutInFlow(timeout);
        return timeout;
    }

    /// <summary>Removes <paramref name="timeout"/> from the shared pool, wherever other tests' returns have queued it.</summary>
    /// <remarks>Withdraws an untaken plant first, so a request that never rented its timeout is not reported as returning it.</remarks>
    /// <returns>Whether <paramref name="timeout"/> was in the pool.</returns>
    internal static bool TryTakeFromPool(CancellationTokenSource timeout)
    {
        JsonRpcConfigExtension.UseForNextTimeoutInFlow(null);
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
        JsonRpcConfigExtension.UseForNextTimeoutInFlow(null);
        // Only a source taken back from the pool is safe to dispose: one left there would fail the next rent anywhere
        // in the process, and one another test rented is theirs to return.
        if (timeout.DisposeCount == 0 && TryTakeFromPool(timeout))
        {
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
