// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using TaskCompletionSource = DotNetty.Common.Concurrency.TaskCompletionSource;

namespace Nethermind.Consensus.Test.Scheduler;

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
        TaskCompletionSource tcs = new TaskCompletionSource();
        await using BackgroundTaskScheduler scheduler = new BackgroundTaskScheduler(_branchProcessor, _chainHeadInfo, 1, 65536, LimboLogs.Instance);

        scheduler.TryScheduleTask(1, (_, token) =>
        {
            tcs.SetResult(1);
            return Task.CompletedTask;
        });

        await tcs.Task;
    }

    [Test]
    public async Task Test_task_will_execute_concurrently_when_configured_so()
    {
        await using BackgroundTaskScheduler scheduler = new BackgroundTaskScheduler(_branchProcessor, _chainHeadInfo, 2, 65536, LimboLogs.Instance);

        int counter = 0;

        SemaphoreSlim waitSignal = new SemaphoreSlim(0);
        scheduler.TryScheduleTask(1, async (_, token) =>
        {
            Interlocked.Increment(ref counter);
            await waitSignal.WaitAsync(token);
            Interlocked.Decrement(ref counter);
        });
        scheduler.TryScheduleTask(1, async (_, token) =>
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
        await using BackgroundTaskScheduler scheduler = new BackgroundTaskScheduler(_branchProcessor, _chainHeadInfo, 2, 65536, LimboLogs.Instance);

        bool wasCancelled = false;

        ManualResetEvent waitSignal = new ManualResetEvent(false);
        scheduler.TryScheduleTask(1, async (_, token) =>
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
    [Retry(3)]
    public async Task Test_task_that_is_scheduled_during_block_processing_will_continue_after()
    {
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 2, 65536, LimboLogs.Instance);
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        int executionCount = 0;
        for (int i = 0; i < 5; i++)
        {
            scheduler.TryScheduleTask(1, (_, token) =>
            {
                executionCount++;
                return Task.CompletedTask;
            });
        }

        await Task.Delay(10);
        executionCount.Should().Be(0);

        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));
        Assert.That(() => executionCount, Is.EqualTo(5).After(1000, 10));
    }

    [Test]
    public async Task Test_task_that_is_scheduled_during_block_processing_but_deadlined_will_get_called_and_cancelled()
    {
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 2, 65536, LimboLogs.Instance);
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        bool wasCancelled = false;
        ManualResetEvent waitSignal = new ManualResetEvent(false);
        scheduler.TryScheduleTask(1, (_, token) =>
        {
            wasCancelled = token.IsCancellationRequested;
            waitSignal.Set();
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(1));

        await Task.Delay(10);
        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));
        (await waitSignal.WaitOneAsync(CancellationToken.None)).Should().BeTrue();

        wasCancelled.Should().BeTrue();
    }

    [Test]
    public async Task Test_expired_tasks_are_drained_during_block_processing()
    {
        int capacity = 16;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 1, capacity, LimboLogs.Instance);

        // Start block processing — signal is reset, token cancelled
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        int cancelledCount = 0;
        for (int i = 0; i < capacity; i++)
        {
            scheduler.TryScheduleTask(1, (_, token) =>
            {
                if (token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref cancelledCount);
                }
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(1));
        }

        // Expired tasks should be drained even while block processing is in progress
        Assert.That(() => cancelledCount, Is.EqualTo(capacity).After(2000, 10));

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
            scheduler.TryScheduleTask(1, (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(1)).Should().BeTrue();
        }

        // Wait for deadlines to pass and expired tasks to be drained
        await Task.Delay(200);

        // New tasks should be accepted because expired tasks freed up queue space
        for (int i = 0; i < capacity; i++)
        {
            bool accepted = scheduler.TryScheduleTask(1, (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(1));
            accepted.Should().BeTrue($"Task {i} should be accepted after expired tasks were drained");
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
        int cancelledCount = 0;
        int droppedCount = 0;

        // --- Phase 1: Fill the queue to capacity during block processing ---
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        for (int i = 0; i < capacity; i++)
        {
            bool accepted = scheduler.TryScheduleTask(1, (_, token) =>
            {
                if (token.IsCancellationRequested)
                    Interlocked.Increment(ref cancelledCount);
                else
                    Interlocked.Increment(ref executedCount);
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(10));
            accepted.Should().BeTrue($"Phase 1: task {i} should be accepted up to capacity");
        }

        // Next task should be dropped — queue is at capacity
        bool overCapacity = scheduler.TryScheduleTask(1, (_, _) => Task.CompletedTask, TimeSpan.FromMilliseconds(1));
        overCapacity.Should().BeFalse("task beyond capacity should be dropped");
        droppedCount++;

        // Wait for deadlines to expire and tasks to drain
        Assert.That(
            () => Volatile.Read(ref cancelledCount),
            Is.EqualTo(capacity).After(5000, 10),
            "all tasks should be drained with cancelled tokens during block processing");

        // --- Phase 2: End block processing, verify queue accepts tasks and runs them normally ---
        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

        Interlocked.Exchange(ref executedCount, 0);

        int phase2Count = capacity / 2;
        for (int i = 0; i < phase2Count; i++)
        {
            bool accepted = scheduler.TryScheduleTask(1, (_, _) =>
            {
                Interlocked.Increment(ref executedCount);
                return Task.CompletedTask;
            });
            accepted.Should().BeTrue($"Phase 2: task {i} should be accepted after queue drained");
        }

        Assert.That(
            () => Volatile.Read(ref executedCount),
            Is.EqualTo(phase2Count).After(5000, 10),
            "all phase 2 tasks should execute normally after block processing ends");

        // --- Phase 3: Another block processing cycle with mixed short and long timeouts ---
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        int phase3CancelledCount = 0;
        int phase3ExecutedCount = 0;

        // Short-lived tasks (will expire during block processing)
        int shortLivedCount = capacity / 2;
        for (int i = 0; i < shortLivedCount; i++)
        {
            scheduler.TryScheduleTask(1, (_, token) =>
            {
                if (token.IsCancellationRequested)
                    Interlocked.Increment(ref phase3CancelledCount);
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(5)).Should().BeTrue($"Phase 3: short-lived task {i} should be accepted");
        }

        // Long-lived tasks (will survive until block processing ends)
        int longLivedCount = capacity / 4;
        for (int i = 0; i < longLivedCount; i++)
        {
            scheduler.TryScheduleTask(1, (_, token) =>
            {
                if (!token.IsCancellationRequested)
                    Interlocked.Increment(ref phase3ExecutedCount);
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(30)).Should().BeTrue($"Phase 3: long-lived task {i} should be accepted");
        }

        // Wait for short-lived tasks to expire and drain
        Assert.That(
            () => Volatile.Read(ref phase3CancelledCount),
            Is.EqualTo(shortLivedCount).After(5000, 10),
            "short-lived tasks should drain with cancelled tokens during block processing");

        // Long-lived tasks should not have executed yet (still waiting for block processing to end)
        Volatile.Read(ref phase3ExecutedCount).Should().Be(0,
            "long-lived tasks should wait during block processing");

        // End block processing — long-lived tasks should now execute
        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

        Assert.That(
            () => Volatile.Read(ref phase3ExecutedCount),
            Is.EqualTo(longLivedCount).After(5000, 10),
            "long-lived tasks should execute after block processing ends");

        // --- Phase 4: Verify queue is fully operational with one more fill-and-drain ---
        Interlocked.Exchange(ref executedCount, 0);

        for (int i = 0; i < capacity; i++)
        {
            scheduler.TryScheduleTask(1, (_, _) =>
            {
                Interlocked.Increment(ref executedCount);
                return Task.CompletedTask;
            }).Should().BeTrue($"Phase 4: task {i} should be accepted in fully recovered queue");
        }

        Assert.That(
            () => Volatile.Read(ref executedCount),
            Is.EqualTo(capacity).After(5000, 10),
            "all tasks in the final phase should execute successfully");

        droppedCount.Should().Be(1, "only the one over-capacity task should have been dropped across all phases");
    }
}
