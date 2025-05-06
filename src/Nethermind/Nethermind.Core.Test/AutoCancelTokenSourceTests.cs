// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Utils;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class AutoCancelTokenSourceTests
{
    [Test]
    public void AutoCancelOnExitClosure()
    {
        CancellationToken TaskWithInnerCancellation(CancellationToken token)
        {
            using AutoCancelTokenSource cts = token.CreateChildTokenSource();
            return cts.Token;
        }

        TaskWithInnerCancellation(default).IsCancellationRequested.Should().BeTrue();
    }

    [Test]
    public void AutoCancelPropagateParentCancellation()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();

        using AutoCancelTokenSource acts = cts.Token.CreateChildTokenSource();

        acts.Token.IsCancellationRequested.Should().BeFalse();

        cts.Cancel();

        acts.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Test]
    public void When_a_task_failed_cancel_other_task_and_forward_the_right_exception()
    {
        using AutoCancelTokenSource cts = new AutoCancelTokenSource();

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

        var act = () => cts.WhenAllSucceed(failedTask, okTask, operationCancelledTask);
        act.Should().ThrowAsync<InvalidOperationException>();
    }
}
