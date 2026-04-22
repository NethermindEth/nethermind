// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[Parallelizable(ParallelScope.Self)]
public class TimeoutUtilsTests
{
    [Test]
    public async Task TimeoutOn_fast_task_completes_and_cts_disposes_safely()
    {
        using CancellationTokenSource cts = new();
        Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cts.Token);

        int result = await Task.FromResult(42).TimeoutOn(timeoutTask, cts);

        Assert.That(result, Is.EqualTo(42));
    }

    [Test]
    public void TimeoutOn_already_completed_timeout_throws_TimeoutException()
    {
        using CancellationTokenSource cts = new();
        TaskCompletionSource<int> neverCompletes = new();

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await neverCompletes.Task.TimeoutOn(Task.CompletedTask, cts));
    }
}
