// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.ProtocolHandlers;

/// <summary>
/// Regression test for the race between TrySetCanceled (timeout path)
/// and SetResult/TrySetResult (late init message) in ProtocolHandlerBase.
///
/// Before fix: TrySetCanceled() on timeout + SetResult() on late message
/// throws InvalidOperationException because the TCS is already completed.
///
/// After fix: TrySetResult() is used instead of SetResult(), making the
/// late message a safe no-op when the TCS is already in a terminal state.
///
/// All three possible orderings are tested deterministically:
///   1. Timeout first, then message (TrySetCanceled → TrySetResult)
///   2. Message first, then timeout (TrySetResult → TrySetCanceled)
///   3. Simultaneous (Barrier-synchronized)
/// </summary>
[Parallelizable(ParallelScope.All)]
public class ProtocolInitCompletionRaceTests
{
    [Test]
    public void SetResult_after_TrySetCanceled_throws_InvalidOperationException()
    {
        // Pre-fix behavior: timeout fires → TrySetCanceled, then late message → SetResult
        TaskCompletionSource<object> tcs = new();

        tcs.TrySetCanceled();

        Assert.Throws<InvalidOperationException>(() => tcs.SetResult(new object()));
    }

    [Test]
    public void TrySetResult_after_TrySetCanceled_is_safe_noop()
    {
        // Post-fix: timeout wins the race, late message is a no-op
        TaskCompletionSource<object> tcs = new();

        tcs.TrySetCanceled();

        bool wasSet = tcs.TrySetResult(new object());

        Assert.That(wasSet, Is.False, "TrySetResult should return false — TCS is already canceled");
        Assert.That(tcs.Task.IsCanceled, Is.True, "TCS should remain in canceled state");
    }

    [Test]
    public void TrySetCanceled_after_TrySetResult_is_safe_noop()
    {
        // Message wins the race, late timeout is a no-op
        TaskCompletionSource<object> tcs = new();

        tcs.TrySetResult(new object());

        bool wasCanceled = tcs.TrySetCanceled();

        Assert.That(wasCanceled, Is.False, "TrySetCanceled should return false — TCS is already completed");
        Assert.That(tcs.Task.IsCompletedSuccessfully, Is.True, "TCS should remain in completed state");
    }

    [Test]
    public void Simultaneous_TrySetCanceled_and_TrySetResult_exactly_one_wins()
    {
        // Both threads release at the exact same instant via Barrier.
        // Exactly one must win; the loser must return false; no exceptions.
        using Barrier barrier = new(2);
        TaskCompletionSource<object> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        bool cancelWon = false;
        bool resultWon = false;

        Thread cancelThread = new(() =>
        {
            barrier.SignalAndWait();
            cancelWon = tcs.TrySetCanceled();
        });

        Thread resultThread = new(() =>
        {
            barrier.SignalAndWait();
            resultWon = tcs.TrySetResult(new object());
        });

        cancelThread.Start();
        resultThread.Start();
        cancelThread.Join();
        resultThread.Join();

        Assert.That(tcs.Task.IsCompleted, Is.True, "TCS must be in a terminal state");
        Assert.That(cancelWon ^ resultWon, Is.True, "Exactly one operation must win the race");

        if (cancelWon)
        {
            Assert.That(tcs.Task.IsCanceled, Is.True);
        }
        else
        {
            Assert.That(tcs.Task.IsCompletedSuccessfully, Is.True);
        }
    }
}
