// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Tasks;
using NUnit.Framework;

namespace Nethermind.Core.Test.Tasks;

public class WaitAnyWhereTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Single_result_is_forwarded_undisposed([Values] bool accepted)
    {
        // Accepted or not, the only result is forwarded: the caller owns it either way.
        Disposable onlyResult = new();

        Disposable result = await Wait.AnyWhere(_ => accepted, Task.FromResult(onlyResult));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.SameAs(onlyResult));
            Assert.That(onlyResult.DisposeCount, Is.Zero);
        }
    }

    [Test]
    public async Task Rejected_result_is_disposed()
    {
        Disposable rejected = new();
        Disposable accepted = new();

        Disposable result = await Wait.AnyWhere(
            r => ReferenceEquals(r, accepted),
            Task.FromResult(rejected),
            Task.FromResult(accepted));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.SameAs(accepted));
            // Exactly once: the rejected path and the abandoned path both reach `Discard`, and only
            // the removal from the set keeps them from both reaching it for the same result.
            Assert.That(rejected.DisposeCount, Is.EqualTo(1));
            Assert.That(accepted.DisposeCount, Is.Zero);
        }
    }

    [Test]
    public async Task Result_of_a_task_abandoned_in_flight_is_disposed()
    {
        Disposable winner = new();
        Disposable straggler = new();
        TaskCompletionSource<Disposable> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Disposable result = await Wait.AnyWhere(r => r is not null, Task.FromResult(winner), pending.Task);

        Assert.That(result, Is.SameAs(winner));

        // The straggler produces its result only after the winner has already been forwarded.
        pending.SetResult(straggler);

        await straggler.WaitForDisposal();
        Assert.That(winner.DisposeCount, Is.Zero);
    }

    [Test]
    public async Task Result_of_a_task_abandoned_by_a_failure_is_disposed([Values] bool cancelled)
    {
        Disposable straggler = new();
        TaskCompletionSource<Disposable> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Disposable> failed = cancelled
            ? Task.FromCanceled<Disposable>(new CancellationToken(true))
            : Task.FromException<Disposable>(new InvalidOperationException());
        Task<Disposable> anyWhere = Wait.AnyWhere(
            r => r is not null,
            failed,
            pending.Task);

        Assert.ThrowsAsync(cancelled ? typeof(TaskCanceledException) : typeof(InvalidOperationException), () => anyWhere);

        // The straggler produces its result only after the failure has already unwound the call.
        pending.SetResult(straggler);

        await straggler.WaitForDisposal();
    }

    [Test]
    public async Task Throwing_predicate_disposes_current_and_abandoned_results()
    {
        Disposable current = new();
        Disposable straggler = new();
        TaskCompletionSource<Disposable> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Disposable> anyWhere = Wait.AnyWhere(
            _ => throw new InvalidOperationException(), Task.FromResult(current), pending.Task);

        Assert.ThrowsAsync<InvalidOperationException>(() => anyWhere);
        Assert.That(current.DisposeCount, Is.EqualTo(1));

        pending.SetResult(straggler);

        await straggler.WaitForDisposal();
    }

    [Test]
    public async Task Disposable_results_typed_as_object_are_disposed([Values] bool rejected)
    {
        Disposable discarded = new();
        object accepted = new();
        TaskCompletionSource<object> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (rejected) pending.SetResult(discarded);

        object result = await Wait.AnyWhere(
            r => ReferenceEquals(r, accepted), pending.Task, Task.FromResult(accepted));

        Assert.That(result, Is.SameAs(accepted));
        if (!rejected) pending.SetResult(discarded);

        await discarded.WaitForDisposal();
    }

    [Test]
    public async Task Non_disposable_results_are_forwarded_unchanged()
    {
        byte[] accepted = [1];

        byte[]? result = await Wait.AnyWhere(
            r => r is not null,
            Task.FromResult<byte[]?>(null),
            Task.FromResult<byte[]?>(accepted));

        Assert.That(result, Is.SameAs(accepted));
    }

    /// <summary>A result whose disposal can be awaited rather than polled for.</summary>
    private sealed class Disposable : IDisposable
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            _disposed.TrySetResult();
        }

        /// <summary>Completes once this instance has been disposed; the timeout is the failure guard.</summary>
        /// <remarks>
        /// `AnyWhere` disposes an abandoned result from a continuation it does not wait for, so the
        /// disposal is asynchronous. Awaiting the signal keeps that deterministic rather than betting
        /// on a deadline a loaded worker can miss.
        /// </remarks>
        public async Task WaitForDisposal() =>
            Assert.That(await Task.WhenAny(_disposed.Task, Task.Delay(WaitTimeout)), Is.SameAs(_disposed.Task),
                "the result was not disposed");
    }
}
