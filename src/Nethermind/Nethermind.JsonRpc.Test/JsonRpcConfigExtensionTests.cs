// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[NonParallelizable]
public class JsonRpcConfigExtensionTests
{
    [Test]
    public void Rented_timeout_returns_to_pool_unless_cancelled([Values] bool cancelled)
    {
        JsonRpcConfig config = new();
        TimeoutTestHelper.TrackingCancellationTokenSource timeout = TimeoutTestHelper.RentTrackingTimeoutSourceForNextRequest();
        try
        {
            using (RentedTimeout rented = config.RentTimeout())
            {
                Assert.That(rented.Token, Is.EqualTo(timeout.Token));
                if (cancelled) timeout.Cancel();
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(timeout.DisposeCount, Is.EqualTo(cancelled ? 1 : 0));
                Assert.That(TimeoutTestHelper.WasReturned(timeout), Is.EqualTo(!cancelled));
                Assert.That(timeout.IsCancellationRequested, Is.EqualTo(cancelled));
            }
        }
        finally
        {
            TimeoutTestHelper.DisposeIfNotAlreadyObserved(timeout);
        }
    }

    [Test]
    public void Pooled_timeout_is_armed_with_the_configured_timeout()
    {
        Assume.That(Debugger.IsAttached, Is.False, "a debugger gets an unarmed timeout source");
        JsonRpcConfigExtension.ReturnTimeoutCancellationToken(new CancellationTokenSource());
        using CancellationTokenSource rented = new JsonRpcConfig { Timeout = 0 }.BuildTimeoutCancellationToken();
        Assert.That(rented.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)), Is.True);
    }
}
