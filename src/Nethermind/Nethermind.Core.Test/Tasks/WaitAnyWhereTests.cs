// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Tasks;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.Tasks;

public class WaitAnyWhereTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private ILogManager _originalLogManager = null!;

    [SetUp]
    public void SetUp() => _originalLogManager = Static.LogManager;

    [TearDown]
    public void TearDown() => Static.LogManager = _originalLogManager;

    [Test]
    public async Task Disposal_failure_is_logged_without_blocking_completion([Values] bool rejected)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsError.Returns(true);
        TaskCompletionSource<Exception> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseLogger = new();
        TaskCompletionSource logged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.When(log => log.Error(Arg.Any<string>(), Arg.Any<Exception>()))
            .Do(call =>
            {
                observed.TrySetResult(call.ArgAt<Exception>(1));
                releaseLogger.Wait(WaitTimeout * 3);
                logged.SetResult();
            });
        Static.LogManager = new WaitLogManager(_originalLogManager, () => new ILogger(logger));
        Disposable winner = new();
        ThrowingDisposable discarded = new();
        // Inline continuations expose logging on the producer's completion thread.
#pragma warning disable NETH006
        TaskCompletionSource<IDisposable> pending = new();
#pragma warning restore NETH006
        TaskCompletionSource<IDisposable> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!rejected) accepted.SetResult(winner);
        Task<IDisposable> operation = Task.Run(async () =>
        {
            if (rejected) pending.SetResult(discarded);
            IDisposable result = await Wait.AnyWhere<IDisposable>(r =>
            {
                if (ReferenceEquals(r, discarded)) accepted.SetResult(winner);
                return ReferenceEquals(r, winner);
            }, pending.Task, accepted.Task);
            if (!rejected) pending.SetResult(discarded);
            return result;
        });
        try
        {
            await AssertCompleted(observed.Task, "the disposal failure was not logged");
            await AssertCompleted(operation, "logging blocked result forwarding or the task completion thread");
            IDisposable result = await operation;
            Exception error = await observed.Task;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(error, Is.TypeOf<InvalidOperationException>());
                Assert.That(result, Is.SameAs(winner));
                Assert.That(winner.DisposeCount, Is.Zero);
                Assert.That(discarded.DisposeCount, Is.EqualTo(1));
            }
        }
        finally
        {
            releaseLogger.Set();
            await AssertCompleted(logged.Task, "the logger did not finish");
        }
    }

    [Test]
    public async Task Logging_failures_are_observed([Values] bool managerThrows)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsError.Returns(true);
        logger.When(log => log.Error(Arg.Any<string>(), Arg.Any<Exception>()))
            .Do(_ => throw new InvalidOperationException("logger failure"));
        int requests = 0;
        Static.LogManager = new WaitLogManager(_originalLogManager, () =>
        {
            Interlocked.Increment(ref requests);
            return managerThrows ? throw new InvalidOperationException("manager failure") : new ILogger(logger);
        });

        Task reporting = Wait.ReportDiscardFailure(new InvalidOperationException("disposal failure"));
        await reporting.WaitAsync(WaitTimeout);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reporting.IsCompletedSuccessfully, Is.True);
            Assert.That(requests, Is.EqualTo(1));
        }
        if (!managerThrows) logger.Received(1).Error(Arg.Any<string>(), Arg.Any<Exception>());
    }

    private sealed class WaitLogManager(ILogManager original, Func<ILogger> getLogger) : ILogManager
    {
        public ILogger GetClassLogger<T>() => original.GetClassLogger<T>();
        public ILogger GetLogger(string loggerName) => loggerName == nameof(Wait) ? getLogger() : original.GetLogger(loggerName);
    }

    private static async Task AssertCompleted(Task task, string message) =>
        Assert.That(await Task.WhenAny(task, Task.Delay(WaitTimeout)), Is.SameAs(task), message);

    private sealed class ThrowingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose()
        {
            DisposeCount++;
            throw new InvalidOperationException("disposal failure");
        }
    }

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
