// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
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

        AssertExited(exitSource, 7);
    }

    [Test]
    public void Cancellation_exits_with_sigint_by_default()
    {
        using CancellationTokenSource cancellation = new();
        ProcessExitSource exitSource = new(cancellation.Token);

        cancellation.Cancel();

        AssertExited(exitSource, ExitCodes.SigInt);
    }

    [Test]
    public void Cancellation_exits_with_the_code_given_at_that_moment()
    {
        using CancellationTokenSource cancellation = new();
        int exitCode = ExitCodes.SigInt;
        ProcessExitSource exitSource = new(cancellation.Token, () => exitCode);

        exitCode = ExitCodes.SigTerm;
        cancellation.Cancel();

        AssertExited(exitSource, ExitCodes.SigTerm);
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

    [Test]
    public void Token_after_exit_is_cancelled_and_usable()
    {
        ProcessExitSource exitSource = new(CancellationToken.None);

        exitSource.Exit(0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitSource.Token.IsCancellationRequested, Is.True);
            Assert.That(exitSource.Token.WaitHandle.WaitOne(0), Is.True);
        }
    }

    [Test]
    public void Token_read_from_a_source_that_exit_disposed_is_cancelled()
    {
        ProcessExitSource exitSource = new(CancellationToken.None);
        FieldInfo sourceField = typeof(ProcessExitSource).GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? source = sourceField.GetValue(exitSource);
        exitSource.Exit(0);

        // What a reader sees when it loaded the source just before Exit swapped it out, cancelled and disposed it.
        sourceField.SetValue(exitSource, source);
        try
        {
            CancellationToken token = default;

            Assert.That(() => token = exitSource.Token, Throws.Nothing);
            Assert.That(token.IsCancellationRequested, Is.True);
        }
        finally
        {
            sourceField.SetValue(exitSource, null);
        }
    }

    private static void AssertExited(ProcessExitSource exitSource, int expectedExitCode)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitSource.ExitCode, Is.EqualTo(expectedExitCode));
            Assert.That(exitSource.ExitTask.IsCompletedSuccessfully, Is.True);
        }
    }
}
