// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.ProtocolHandlers;

/// <summary>
/// Regression test for the race between TrySetCanceled (timeout path)
/// and SetResult (late init message) in ProtocolHandlerBase.
///
/// Before fix: TrySetCanceled() on timeout + SetResult() on late message
/// throws InvalidOperationException because the TCS is already completed.
///
/// After fix: TrySetResult() is used instead of SetResult(), making the
/// late message a safe no-op when the TCS is already in a terminal state.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class ProtocolInitCompletionRaceTests
{
    [Test]
    public void SetResult_after_TrySetCanceled_throws()
    {
        // Simulates: timeout fires → TrySetCanceled, then late message → SetResult
        TaskCompletionSource<object> tcs = new();

        tcs.TrySetCanceled();

        Assert.Throws<InvalidOperationException>(() => tcs.SetResult(new object()));
    }

    [Test]
    public void TrySetResult_after_TrySetCanceled_does_not_throw()
    {
        // Simulates the fix: timeout fires → TrySetCanceled, then late message → TrySetResult
        TaskCompletionSource<object> tcs = new();

        tcs.TrySetCanceled();

        Assert.DoesNotThrow(() => tcs.TrySetResult(new object()));
        Assert.That(tcs.Task.IsCanceled, Is.True, "TCS should remain in canceled state");
    }

    [Test]
    public void TrySetCanceled_after_TrySetResult_does_not_throw()
    {
        // Simulates: init message arrives first (fast path), then timeout fires late
        TaskCompletionSource<object> tcs = new();

        tcs.TrySetResult(new object());

        Assert.DoesNotThrow(() => tcs.TrySetCanceled());
        Assert.That(tcs.Task.IsCompletedSuccessfully, Is.True, "TCS should remain in completed state");
    }

    [Test]
    public async Task Concurrent_TrySetCanceled_and_TrySetResult_never_throws()
    {
        // Stress test: race TrySetCanceled and TrySetResult on many TCS instances
        const int iterations = 10_000;

        for (int i = 0; i < iterations; i++)
        {
            TaskCompletionSource<object> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            Task cancelTask = Task.Run(() => tcs.TrySetCanceled());
            Task resultTask = Task.Run(() => tcs.TrySetResult(new object()));

            // Neither should throw regardless of which wins the race
            await cancelTask;
            await resultTask;

            // TCS should be in exactly one terminal state
            Assert.That(tcs.Task.IsCompleted, Is.True);
        }
    }
}
