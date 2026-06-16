// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using TaskCompletionSource = DotNetty.Common.Concurrency.TaskCompletionSource;

namespace Nethermind.Consensus.Test.Scheduler;

internal readonly struct TestRequest : IBackgroundTaskRequest<TestRequest>
{
    public static int TaskId => BackgroundTaskTypeId<TestRequest>.Id;
}

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class BackgroundTaskSchedulerTests
{
    private IBranchProcessor _branchProcessor;
    private IChainHeadInfoProvider _chainHeadInfo;

    [SetUp]
    public void Setup()
    {
        _branchProcessor = Substitute.For<IBranchProcessor>();
        _chainHeadInfo = Substitute.For<IChainHeadInfoProvider>();
        _chainHeadInfo.IsSyncing.Returns(false);
    }

    [Test]
    public async Task Test_task_will_execute()
    {
        TaskCompletionSource tcs = new();
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 1, 65536, LimboLogs.Instance);

        scheduler.TryScheduleTask(default(TestRequest), (_, token) =>
        {
            tcs.SetResult(1);
            return Task.CompletedTask;
        });

        await tcs.Task;
    }

    [Test]
    public async Task DisposeAsync_should_complete_when_scheduler_is_idle()
    {
        BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 1, 65536, LimboLogs.Instance);

        Assert.DoesNotThrowAsync(
            async () => await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)),
            "DisposeAsync did not complete within timeout - possible deadlock in background task scheduler");
    }

    [Test]
    public async Task Test_task_will_execute_concurrently_when_configured_so()
    {
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 2, 65536, LimboLogs.Instance);

        int counter = 0;

        SemaphoreSlim waitSignal = new(0);
        scheduler.TryScheduleTask(default(TestRequest), async (_, token) =>
        {
            Interlocked.Increment(ref counter);
            await waitSignal.WaitAsync(token);
            Interlocked.Decrement(ref counter);
        });
        scheduler.TryScheduleTask(default(TestRequest), async (_, token) =>
        {
            Interlocked.Increment(ref counter);
            await waitSignal.WaitAsync(token);
            Interlocked.Decrement(ref counter);
        });

        Assert.That(() => counter, Is.EqualTo(2).After(5000, 1));
        waitSignal.Release(2);
    }

    [Test]
    public async Task Test_task_will_cancel_on_block_processing()
    {
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 2, 65536, LimboLogs.Instance);

        bool wasCancelled = false;

        ManualResetEvent waitSignal = new(false);
        scheduler.TryScheduleTask(default(TestRequest), async (_, token) =>
        {
            waitSignal.Set();
            try
            {
                await Task.Delay(100000, token);
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
            }
        });

        await waitSignal.WaitOneAsync(CancellationToken.None);
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));
        await Task.Delay(10);
        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));
        Assert.That(() => wasCancelled, Is.EqualTo(true).After(10, 1));
    }

    [Test]
    public async Task Test_task_scheduled_during_block_processing_gets_cancelled_token()
    {
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 2, 65536, LimboLogs.Instance);
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        int cancelledCount = 0;
        CountdownEvent expiredRan = new(5);
        for (int i = 0; i < 5; i++)
        {
            scheduler.TryScheduleTask(default(TestRequest), (_, token) =>
            {
                if (token.IsCancellationRequested)
                    Interlocked.Increment(ref cancelledCount);
                expiredRan.Signal();
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(1));
        }

        Assert.That(expiredRan.Wait(TimeSpan.FromSeconds(30)), Is.True, "Expired tasks did not all run within 30s");
        Assert.That(cancelledCount, Is.EqualTo(5));

        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

        int postBlockCount = 0;
        CountdownEvent postBlockRan = new(3);
        for (int i = 0; i < 3; i++)
        {
            scheduler.TryScheduleTask(default(TestRequest), (_, token) =>
            {
                if (!token.IsCancellationRequested)
                    Interlocked.Increment(ref postBlockCount);
                postBlockRan.Signal();
                return Task.CompletedTask;
            });
        }

        Assert.That(postBlockRan.Wait(TimeSpan.FromSeconds(30)), Is.True, "Post-block tasks did not all run within 30s");
        Assert.That(postBlockCount, Is.EqualTo(3));
    }

    [Test]
    public async Task Test_expired_task_during_block_processing_gets_cancelled_token_and_exits()
    {
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 2, 65536, LimboLogs.Instance);
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        bool wasCancelled = false;
        ManualResetEvent waitSignal = new(false);
        scheduler.TryScheduleTask(default(TestRequest), (_, token) =>
        {
            wasCancelled = token.IsCancellationRequested;
            waitSignal.Set();
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(1));

        Assert.That((await waitSignal.WaitOneAsync(CancellationToken.None)), Is.True);
        Assert.That(wasCancelled, Is.True, "expired task should receive a cancelled token during block processing");

        // After block processing, new tasks execute normally
        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

        ManualResetEvent postBlockSignal = new(false);
        scheduler.TryScheduleTask(default(TestRequest), (_, token) =>
        {
            postBlockSignal.Set();
            return Task.CompletedTask;
        });
        Assert.That((await postBlockSignal.WaitOneAsync(CancellationToken.None)), Is.True);
    }

    [Test]
    public async Task Test_expired_tasks_drain_during_block_processing_freeing_queue_space()
    {
        int capacity = 16;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 1, capacity, LimboLogs.Instance);

        // Start block processing — token cancelled
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        // Fill the queue with tasks that expire in 1ms
        for (int i = 0; i < capacity; i++)
        {
            scheduler.TryScheduleTask(default(TestRequest), (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(1));
        }

        // Expired tasks are drained (run with cancelled token) during block processing, freeing queue space
        await Task.Delay(500);

        // New tasks should be accepted because expired tasks freed up queue space
        for (int i = 0; i < capacity; i++)
        {
            bool accepted = scheduler.TryScheduleTask(default(TestRequest), (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(1));
            Assert.That(accepted, Is.True, $"Task {i} should be accepted after expired tasks freed queue space");
        }

        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));
    }

    [Test]
    public async Task Test_queue_accepts_new_tasks_after_expired_tasks_drain_during_block_processing()
    {
        int capacity = 16;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 1, capacity, LimboLogs.Instance);

        // Start block processing — signal is reset, token cancelled
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        // Fill the queue with short-lived tasks
        for (int i = 0; i < capacity; i++)
        {
            Assert.That(scheduler.TryScheduleTask(default(TestRequest), (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(1)), Is.True);
        }

        // Wait for deadlines to pass and expired tasks to be drained with cancelled tokens
        await Task.Delay(500);

        // New tasks should be accepted because expired tasks freed up queue space
        for (int i = 0; i < capacity; i++)
        {
            bool accepted = scheduler.TryScheduleTask(default(TestRequest), (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(1));
            Assert.That(accepted, Is.True, $"Task {i} should be accepted after expired tasks were drained");
        }

        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));
    }

    [Test]
    public async Task Test_high_capacity_queue_survives_repeated_block_processing_cycles()
    {
        int capacity = 1024;
        int concurrency = 2;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, concurrency, capacity, LimboLogs.Instance);

        int executedCount = 0;

        // --- Phase 1: Fill the queue during block processing — expired tasks drain with cancelled tokens ---
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        for (int i = 0; i < capacity; i++)
        {
            bool accepted = scheduler.TryScheduleTask(default(TestRequest), (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(10));
            Assert.That(accepted, Is.True, $"Phase 1: task {i} should be accepted up to capacity");
        }

        // Wait for expired tasks to drain (consumer wakes every 100ms to check for expired tasks)
        await Task.Delay(2000);

        // --- Phase 2: End block processing, verify queue accepts tasks and runs them normally ---
        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

        int phase2Count = capacity / 2;
        for (int i = 0; i < phase2Count; i++)
        {
            bool accepted = scheduler.TryScheduleTask(default(TestRequest), (_, _) =>
            {
                Interlocked.Increment(ref executedCount);
                return Task.CompletedTask;
            });
            Assert.That(accepted, Is.True, $"Phase 2: task {i} should be accepted after queue drained");
        }

        Assert.That(
            () => Volatile.Read(ref executedCount),
            Is.EqualTo(phase2Count).After(5000, 10),
            "all phase 2 tasks should execute normally after block processing ends");

        // --- Phase 3: Another block processing cycle ---
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        int totalPhase3 = capacity / 2 + capacity / 4;
        for (int i = 0; i < totalPhase3; i++)
        {
            Assert.That(scheduler.TryScheduleTask(default(TestRequest), (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(5)), Is.True, $"Phase 3: task {i} should be accepted");
        }

        // Wait for expired tasks to drain with cancelled tokens
        await Task.Delay(2000);

        // End block processing — verify normal operation with new tasks
        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

        int phase3ExecutedCount = 0;
        int longLivedCount = capacity / 4;
        for (int i = 0; i < longLivedCount; i++)
        {
            scheduler.TryScheduleTask(default(TestRequest), (_, token) =>
            {
                if (!token.IsCancellationRequested)
                    Interlocked.Increment(ref phase3ExecutedCount);
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(30));
        }

        Assert.That(
            () => Volatile.Read(ref phase3ExecutedCount),
            Is.EqualTo(longLivedCount).After(5000, 10),
            "new tasks scheduled after block processing ends should execute normally");

        // --- Phase 4: Verify queue is fully operational with one more fill-and-drain ---
        Interlocked.Exchange(ref executedCount, 0);

        for (int i = 0; i < capacity; i++)
        {
            Assert.That(scheduler.TryScheduleTask(default(TestRequest), (_, _) =>
            {
                Interlocked.Increment(ref executedCount);
                return Task.CompletedTask;
            }), Is.True, $"Phase 4: task {i} should be accepted in fully recovered queue");
        }

        Assert.That(
            () => Volatile.Read(ref executedCount),
            Is.EqualTo(capacity).After(5000, 10),
            "all tasks in the final phase should execute successfully");
    }

    [Test]
    public async Task Stats_are_correctly_reported_when_queue_is_full()
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        int capacity = 10;
        int concurrency = 1;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, concurrency, capacity, new OneLoggerLogManager(new ILogger(logger)));
        for (int i = 0; i < capacity + concurrency + 1; i++)
        {
            scheduler.TryScheduleTask(default(TestRequest), async (_, _) => { await Task.Delay(10); });
        }

        logger.Received()
            .Warn(Arg.Is<string>(static msg =>
                msg.Contains("Background task queue is full")
                && msg.Contains("Capacity: 10")
                && msg.Contains("Stats: (TestRequest:")));
    }

    [Test]
    [Retry(3)]
    public async Task Stats_are_correctly_reported_when_queue_is_empty()
    {
        const int capacity = 5;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, capacity, capacity, LimboLogs.Instance);
        for (int i = 0; i < 2 * capacity; i++)
        {
            scheduler.TryScheduleTask(default(TestRequest), async (_, _) => { await Task.Delay(10); });
        }

        Assert.That(scheduler.GetStats()[nameof(TestRequest)], Is.InRange(capacity - 2, capacity + 2));
        Assert.That(() => scheduler.GetStats()[nameof(TestRequest)], Is.EqualTo(0).After(250, 50));
    }

}
