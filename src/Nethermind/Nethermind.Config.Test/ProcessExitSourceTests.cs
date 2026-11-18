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

    [Test]
    public void Cancellation_exits_with_sigint_by_default()
    {
        using CancellationTokenSource cancellation = new();
        ProcessExitSource exitSource = new(cancellation.Token);

        cancellation.Cancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitSource.ExitCode, Is.EqualTo(ExitCodes.SigInt));
            Assert.That(exitSource.ExitTask.IsCompletedSuccessfully, Is.True);
        }
    }

    [Test]
    public void Cancellation_exits_with_the_code_given_at_that_moment()
    {
        using CancellationTokenSource cancellation = new();
        int exitCode = ExitCodes.SigInt;
        ProcessExitSource exitSource = new(cancellation.Token, () => exitCode);

        exitCode = ExitCodes.SigTerm;
        cancellation.Cancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitSource.ExitCode, Is.EqualTo(ExitCodes.SigTerm));
            Assert.That(exitSource.ExitTask.IsCompletedSuccessfully, Is.True);
        }
    }

    [Test]
    public void Exit_keeps_its_code_when_cancelled_or_exited_again_later()
    {
        using CancellationTokenSource cancellation = new();
        ProcessExitSource exitSource = new(cancellation.Token, static () => ExitCodes.SigTerm);

        exitSource.Exit(7);
        cancellation.Cancel();
        exitSource.Exit(ExitCodes.SigTerm);

        Assert.That(exitSource.ExitCode, Is.EqualTo(7));
    }
}
