// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using NUnit.Framework;

namespace Nethermind.Config.Test;

public class ProcessExitSourceTests
{
    [Test]
    public void Exit_completes_even_when_a_cancellation_callback_throws()
    {
        ProcessExitSource exitSource = new(CancellationToken.None);
        exitSource.Token.Register(static () => throw new InvalidOperationException());

        Assert.That(() => exitSource.Exit(7), Throws.InstanceOf<AggregateException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitSource.ExitCode, Is.EqualTo(7));
            Assert.That(exitSource.ExitTask.IsCompletedSuccessfully, Is.True);
        }
    }
}
