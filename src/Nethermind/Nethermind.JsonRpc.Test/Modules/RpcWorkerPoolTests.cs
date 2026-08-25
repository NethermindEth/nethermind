// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Modules;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class RpcWorkerPoolTests
{
    private const string Prefix = "TestRpcPool";
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Runs_work_on_named_background_threads_below_normal_priority()
    {
        using RpcWorkerPool pool = new(Prefix, 1);

        Thread worker = (Thread)(await pool.RunAsync(static () => Thread.CurrentThread))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(worker.Name, Does.StartWith($"{Prefix}-"));
            Assert.That(worker.IsBackground, Is.True);
            Assert.That(worker.Priority, Is.EqualTo(ThreadPriority.BelowNormal));
        }
    }

    [Test]
    public async Task Runs_at_most_worker_count_invocations_concurrently()
    {
        const int workers = 2;
        using RpcWorkerPool pool = new(Prefix, workers);
        using ManualResetEventSlim release = new();
        int running = 0;

        Task<object?>[] items = new Task<object?>[workers + 1];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = pool.RunAsync(() =>
            {
                Interlocked.Increment(ref running);
                release.Wait(WaitBudget);
                Interlocked.Decrement(ref running);
                return null;
            });
        }

        await WaitUntil(() => Volatile.Read(ref running) == workers);
        await Task.Delay(100);
        Assert.That(Volatile.Read(ref running), Is.EqualTo(workers), "third item started while both workers were busy");

        release.Set();
        await Task.WhenAll(items).WaitAsync(WaitBudget);
    }

    [Test]
    public void Exception_from_work_propagates_unchanged()
    {
        using RpcWorkerPool pool = new(Prefix, 1);

        InvalidOperationException? thrown = Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.RunAsync(static () => throw new InvalidOperationException("boom")));

        Assert.That(thrown!.Message, Is.EqualTo("boom"));
    }

    [Test]
    public async Task Flows_execution_context_to_the_worker()
    {
        using RpcWorkerPool pool = new(Prefix, 1);
        AsyncLocal<int> ambient = new() { Value = 42 };

        object? observed = await pool.RunAsync(() => ambient.Value);

        Assert.That(observed, Is.EqualTo(42));
    }

    [Test]
    public async Task Dispose_fails_pending_work_fast_and_stops_threads()
    {
        RpcWorkerPool pool = new(Prefix, 1);
        using ManualResetEventSlim release = new();
        Thread? worker = null;
        Task<object?> blocked = pool.RunAsync(() =>
        {
            worker = Thread.CurrentThread;
            release.Wait(WaitBudget);
            return null;
        });
        await WaitUntil(() => worker is not null);
        Task<object?> pending = pool.RunAsync(static () => null);

        Task disposing = Task.Run(pool.Dispose);

        Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(WaitBudget), "pending work must fail before the worker is joined");
        Assert.Throws<ObjectDisposedException>(() => pool.RunAsync(static () => null), "new work must be refused after dispose");

        release.Set();
        await disposing.WaitAsync(WaitBudget);
        await blocked.WaitAsync(WaitBudget);
        Assert.That(worker!.IsAlive, Is.False);
    }

    [Test]
    public async Task Failed_thread_start_leaves_the_slot_for_the_next_call()
    {
        int startAttempts = 0;
        using RpcWorkerPool pool = new(Prefix, 1, thread =>
        {
            if (Interlocked.Increment(ref startAttempts) == 1)
            {
                throw new InvalidOperationException("Thread creation failed.");
            }

            thread.Start();
        });

        Assert.Throws<InvalidOperationException>(() => pool.RunAsync(static () => null), "the failed start must surface to the caller, not queue an item nobody consumes");

        object? result = await pool.RunAsync(static () => 7).WaitAsync(WaitBudget);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(7));
            Assert.That(startAttempts, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task Dispose_racing_work_submission_completes_or_refuses_every_item()
    {
        const int iterations = 100;
        const int workers = 4;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            RpcWorkerPool pool = new(Prefix, workers);
            Task<object?>[] submissions = new Task<object?>[workers * 2];
            for (int i = 0; i < submissions.Length; i++)
            {
                submissions[i] = Task.Run(() => pool.RunAsync(static () => null));
            }
            Task disposing = Task.Run(pool.Dispose);

            await disposing.WaitAsync(WaitBudget);
            foreach (Task<object?> submission in submissions)
            {
                // Each item either ran or was refused; an unhandled exception on a worker thread would have crashed the process.
                try
                {
                    await submission.WaitAsync(WaitBudget);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        long deadline = Environment.TickCount64 + (long)WaitBudget.TotalMilliseconds;
        while (!condition())
        {
            Assert.That(Environment.TickCount64, Is.LessThan(deadline), "condition not reached in time");
            await Task.Delay(5);
        }
    }
}
