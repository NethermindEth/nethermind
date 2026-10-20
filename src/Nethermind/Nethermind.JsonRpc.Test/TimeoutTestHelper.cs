// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.JsonRpc.Test;

internal static class TimeoutTestHelper
{
    /// <summary>Creates the source the next timeout rent in the calling async flow will receive.</summary>
    internal static TrackingCancellationTokenSource RentTrackingTimeoutSourceForNextRequest()
    {
        TrackingCancellationTokenSource timeout = new();
        JsonRpcConfigExtension.UseForNextTimeoutInFlow(timeout);
        return timeout;
    }

    /// <summary>Whether a request rented <paramref name="timeout"/> and returned it for reuse.</summary>
    internal static bool WasReturned(CancellationTokenSource timeout) => JsonRpcConfigExtension.EndTimeoutInFlow(timeout);

    internal static void DisposeIfNotAlreadyObserved(TrackingCancellationTokenSource timeout)
    {
        // Only a source returned within the flow is held by nobody: one returned from another flow is in the shared pool,
        // where disposing it would fail the next rent anywhere in the process.
        if (JsonRpcConfigExtension.EndTimeoutInFlow(timeout) && timeout.DisposeCount == 0)
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
