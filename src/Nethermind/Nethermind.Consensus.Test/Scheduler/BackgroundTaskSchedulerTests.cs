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
        TaskCompletionSource tcs = new TaskCompletionSource();
        await using BackgroundTaskScheduler scheduler = new BackgroundTaskScheduler(_branchProcessor, _chainHeadInfo, 1, 65536, LimboLogs.Instance);

        scheduler.TryScheduleTask("test", (_, token) =>
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
        scheduler.TryScheduleTask("test", async (_, token) =>
        {
            Interlocked.Increment(ref counter);
            await waitSignal.WaitAsync(token);
            Interlocked.Decrement(ref counter);
        });
        scheduler.TryScheduleTask("test", async (_, token) =>
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
        scheduler.TryScheduleTask("test", async (_, token) =>
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
            scheduler.TryScheduleTask("test", (_, token) =>
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
        scheduler.TryScheduleTask("test", (_, token) =>
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
    public async Task Stats_are_correctly_reported_when_queue_is_full()
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        int capacity = 10;
        int concurrency = 1;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, concurrency, capacity, new OneLoggerLogManager(new ILogger(logger)));
        for (int i = 0; i < capacity + concurrency + 1; i++)
        {
            scheduler.TryScheduleTask("test", async (_, _) => { await Task.Delay(10); });
        }

        logger.Received()
            .Warn("Background task queue is full (Count: 10, Capacity: 10), dropping task. Stats: (test: 10)");
    }

    [Test]
    [Retry(3)]
    public async Task Stats_are_correctly_reported_when_queue_is_empty()
    {
        const int capacity = 5;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, capacity, capacity, LimboLogs.Instance);
        for (int i = 0; i < 2 * capacity; i++)
        {
            scheduler.TryScheduleTask("test", async (_, _) => { await Task.Delay(10); });
        }

        Assert.That(scheduler.GetStats()["test"], Is.InRange(capacity - 2, capacity + 2));
        Assert.That(() => scheduler.GetStats()["test"], Is.EqualTo(0).After(250, 50));
    }

}
