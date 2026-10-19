// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[NonParallelizable]
public class JsonRpcConfigExtensionTests
{
    [Test]
    public void Rented_timeout_returns_to_pool_unless_cancelled([Values] bool cancelled, [Values] bool otherSourceReturnedMeanwhile)
    {
        JsonRpcConfig config = new();
        TimeoutTestHelper.TrackingCancellationTokenSource timeout = TimeoutTestHelper.RentTrackingTimeoutSourceForNextRequest();
        try
        {
            using (RentedTimeout rented = config.RentTimeout())
            {
                Assert.That(rented.Token, Is.EqualTo(timeout.Token));
                if (cancelled) timeout.Cancel();
                if (otherSourceReturnedMeanwhile) JsonRpcConfigExtension.ReturnTimeoutCancellationToken(new CancellationTokenSource());
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(timeout.DisposeCount, Is.EqualTo(cancelled ? 1 : 0));
                Assert.That(TimeoutTestHelper.TakeFromPool(timeout), Is.EqualTo(!cancelled));
                Assert.That(timeout.IsCancellationRequested, Is.EqualTo(cancelled));
            }
        }
        finally
        {
            TimeoutTestHelper.DisposeIfNotAlreadyObserved(timeout);
        }
    }
}
