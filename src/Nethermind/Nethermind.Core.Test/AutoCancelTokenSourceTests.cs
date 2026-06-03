// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class AutoCancelTokenSourceTests
{
    [Test]
    public void AutoCancelOnExitClosure()
    {
        static CancellationToken TaskWithInnerCancellation(CancellationToken token)
        {
            using AutoCancelTokenSource cts = token.CreateChildTokenSource();
            return cts.Token;
        }

        Assert.That(TaskWithInnerCancellation(default).IsCancellationRequested, Is.True);
    }

    [Test]
    public void AutoCancelPropagateParentCancellation()
    {
        using CancellationTokenSource cts = new();

        using AutoCancelTokenSource acts = cts.Token.CreateChildTokenSource();

        Assert.That(acts.Token.IsCancellationRequested, Is.False);

        cts.Cancel();

        Assert.That(acts.Token.IsCancellationRequested, Is.True);
    }

    [Test]
    public void When_a_task_failed_cancel_other_task_and_forward_the_right_exception()
    {
        using AutoCancelTokenSource cts = new();

        Task failedTask = Task.Run(() =>
        {
            throw new InvalidOperationException();
        });

        Task okTask = Task.Run(async () =>
        {
            await cts.Token.AsTask();
        });

        Task operationCancelledTask = Task.Run(async () =>
        {
            await cts.Token.AsTask();
            cts.Token.ThrowIfCancellationRequested();
        });

        Func<Task> act = () => cts.WhenAllSucceed(failedTask, okTask, operationCancelledTask);
        Assert.That(async () => await act(), Throws.TypeOf<InvalidOperationException>());
    }
}
