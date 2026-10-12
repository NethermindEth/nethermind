// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using TaskExtensions = Nethermind.Core.Extensions.TaskExtensions;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class TaskExtensionsTests
{
    [Test]
    public async Task DelaySafe_preserves_captured_context([Values] bool useTimeSpan)
    {
        using CancellationTokenSource cts = new();
        RecordingContext context = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task<bool> delay;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            delay = useTimeSpan
                ? TaskExtensions.DelaySafe(Timeout.InfiniteTimeSpan, cts.Token)
                : TaskExtensions.DelaySafe(Timeout.Infinite, cts.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await cts.CancelAsync();
        bool result = await delay.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(context.Posts, Is.EqualTo(1));
        }
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public int Posts;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            base.Post(callback, state);
        }
    }

    [Test]
    public async Task DelaySafe_returns_without_throwing(
        [Values] bool useTimeSpan,
        [Values(0, 1, 2)] int cancellation)
    {
        using CancellationTokenSource cts = new();
        if (cancellation == 1) cts.Cancel();
        AsyncLocal<bool> observing = new() { Value = true };
        int exceptions = 0;
        void OnException(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (observing.Value && args.Exception is OperationCanceledException) Interlocked.Increment(ref exceptions);
        }

        bool result;
        AppDomain.CurrentDomain.FirstChanceException += OnException;
        try
        {
            int delay = cancellation == 0 ? 0 : Timeout.Infinite;
            Task<bool> task = useTimeSpan
                ? TaskExtensions.DelaySafe(TimeSpan.FromMilliseconds(delay), cts.Token)
                : TaskExtensions.DelaySafe(delay, cts.Token);
            if (cancellation == 2) await cts.CancelAsync();
            result = await task;
        }
        finally
        {
            observing.Value = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnException;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(cancellation == 0));
            Assert.That(exceptions, Is.Zero);
        }
    }

    [Test]
    public void DelaySafe_rejects_invalid_delay([Values] bool useTimeSpan)
        => Assert.That(async () => await (useTimeSpan
            ? TaskExtensions.DelaySafe(TimeSpan.FromMilliseconds(-2), CancellationToken.None)
            : TaskExtensions.DelaySafe(-2, CancellationToken.None)), Throws.TypeOf<ArgumentOutOfRangeException>());
}
