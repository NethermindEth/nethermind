// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

public class ConcurrencyControllerTests
{
    [Test]
    public void ThreadLimiterWillLimit()
    {
        ConcurrencyController limiter = new(3);

        Assert.That(limiter.TryTakeSlot(out _), Is.EqualTo(true));
        Assert.That(limiter.TryTakeSlot(out _), Is.EqualTo(true));
        Assert.That(limiter.TryTakeSlot(out ConcurrencyController.Slot returner), Is.EqualTo(false));

        returner.Dispose();

        Assert.That(limiter.TryTakeSlot(out _), Is.EqualTo(true));
        Assert.That(limiter.TryTakeSlot(out _), Is.EqualTo(false));
    }

    [Test]
    public void ThreadLimiterWillLimitWithManualRequest()
    {
        ConcurrencyController limiter = new(3);

        Assert.That(limiter.TryRequestConcurrencyQuota(), Is.EqualTo(true));
        Assert.That(limiter.TryRequestConcurrencyQuota(), Is.EqualTo(true));
        Assert.That(limiter.TryRequestConcurrencyQuota(), Is.EqualTo(false));

        limiter.ReturnConcurrencyQuota();

        Assert.That(limiter.TryRequestConcurrencyQuota(), Is.EqualTo(true));
        Assert.That(limiter.TryRequestConcurrencyQuota(), Is.EqualTo(false));
    }
}
