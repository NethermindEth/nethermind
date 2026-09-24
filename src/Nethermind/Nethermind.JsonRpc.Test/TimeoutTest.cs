// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;

namespace Nethermind.JsonRpc.Test;

internal static class TimeoutTest
{
    internal static TrackingCancellationTokenSource RentTrackingTimeoutSourceForNextRequest()
    {
        JsonRpcConfig config = new();
        List<CancellationTokenSource> rentedTimeouts = new(JsonRpcConfigExtension.MaxPoolSize);
        for (int i = 0; i < JsonRpcConfigExtension.MaxPoolSize; i++)
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

    internal static void DisposeIfNotAlreadyObserved(TrackingCancellationTokenSource timeout)
    {
        if (timeout.DisposeCount == 0)
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
