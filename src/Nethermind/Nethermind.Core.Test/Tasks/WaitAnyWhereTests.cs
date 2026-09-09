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
    [Test]
    public async Task Forwarded_result_is_left_to_the_caller()
    {
        Disposable forwarded = new();

        Disposable result = await Wait.AnyWhere(r => r is not null, Task.FromResult(forwarded));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(forwarded));
            Assert.That(forwarded.Disposed, Is.False);
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(accepted));
            Assert.That(rejected.Disposed, Is.True);
            Assert.That(accepted.Disposed, Is.False);
        });
    }

    [Test]
    public async Task Last_rejected_result_is_forwarded_undisposed()
    {
        Disposable onlyResult = new();

        Disposable result = await Wait.AnyWhere(_ => false, Task.FromResult(onlyResult));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.SameAs(onlyResult));
            Assert.That(onlyResult.Disposed, Is.False);
        });
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

        Assert.That(() => straggler.Disposed, Is.True.After(1000, 10));
        Assert.That(winner.Disposed, Is.False);
    }

    [Test]
    public void Result_of_a_task_abandoned_by_a_failure_is_disposed([Values] bool cancelled)
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

        Assert.That(() => straggler.Disposed, Is.True.After(1000, 10));
    }

    [Test]
    public void Throwing_predicate_disposes_current_and_abandoned_results()
    {
        Disposable current = new();
        Disposable straggler = new();
        TaskCompletionSource<Disposable> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Disposable> anyWhere = Wait.AnyWhere(
            _ => throw new InvalidOperationException(), Task.FromResult(current), pending.Task);

        Assert.ThrowsAsync<InvalidOperationException>(() => anyWhere);
        Assert.That(current.Disposed, Is.True);

        pending.SetResult(straggler);

        Assert.That(() => straggler.Disposed, Is.True.After(1000, 10));
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

        Assert.That(() => discarded.Disposed, Is.True.After(1000, 10));
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

    private sealed class Disposable : IDisposable
    {
        private int _disposeCount;

        public bool Disposed => Volatile.Read(ref _disposeCount) > 0;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
