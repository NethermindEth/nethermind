// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Validates that TimeoutOn works correctly when the caller disposes
/// the CancellationTokenSource immediately after the call returns.
/// This is the pattern used by NewPayloadHandler with <c>using CancellationTokenSource cts = new();</c>.
/// </summary>
[Parallelizable(ParallelScope.Self)]
public class TimeoutUtilsTests
{
    [Test]
    public async Task TimeoutOn_task_completes_before_timeout_disposes_cts_safely()
    {
        using CancellationTokenSource cts = new();
        Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
        Task<int> fastTask = Task.FromResult(42);

        int result = await fastTask.TimeoutOn(timeoutTask, cts);

        Assert.That(result, Is.EqualTo(42));
        // CTS is cancelled by TimeoutOn, then disposed by using — no exception
    }

    [Test]
    public void TimeoutOn_timeout_fires_throws_TimeoutException()
    {
        using CancellationTokenSource cts = new();
        Task timeoutTask = Task.CompletedTask; // already completed = instant timeout
        TaskCompletionSource<int> neverCompletes = new();

        Assert.ThrowsAsync<TimeoutException>(async () =>
            await neverCompletes.Task.TimeoutOn(timeoutTask, cts));
        // CTS is NOT cancelled by TimeoutOn on timeout path, but disposed by using — safe
    }

    [Test]
    public async Task TimeoutOn_cts_disposed_after_return_does_not_affect_result()
    {
        // Simulates the NewPayloadHandler pattern: using CTS scoped to try block
        int result;
        {
            using CancellationTokenSource cts = new();
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
            result = await Task.FromResult(99).TimeoutOn(timeoutTask, cts);
            // cts disposed here at end of scope
        }

        Assert.That(result, Is.EqualTo(99));
    }

    [Test]
    public async Task TimeoutOn_without_cts_parameter_works()
    {
        Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));
        Task<int> fastTask = Task.FromResult(7);

        int result = await fastTask.TimeoutOn(timeoutTask);

        Assert.That(result, Is.EqualTo(7));
    }
}
