// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

public class ConcurrencyControllerTests
{
    [Test]
    public void ThreadLimiterWillLimit()
    {
        ConcurrencyController.Slot returner;
        ConcurrencyController limiter = new ConcurrencyController(3);

        limiter.TryTakeSlot(out returner).Should().Be(true);
        limiter.TryTakeSlot(out returner).Should().Be(true);
        limiter.TryTakeSlot(out returner).Should().Be(false);

        returner.Dispose();

        limiter.TryTakeSlot(out returner).Should().Be(true);
        limiter.TryTakeSlot(out returner).Should().Be(false);
    }
}
