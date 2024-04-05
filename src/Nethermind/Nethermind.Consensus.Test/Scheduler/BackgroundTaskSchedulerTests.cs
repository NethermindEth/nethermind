// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using TaskCompletionSource = DotNetty.Common.Concurrency.TaskCompletionSource;

namespace Nethermind.Consensus.Test.Scheduler;

public class BackgroundTaskSchedulerTests
{
    private IBlockProcessor _blockProcessor;

    [SetUp]
    public void Setup()
    {
        _blockProcessor = Substitute.For<IBlockProcessor>();
    }

    [Test]
    public async Task Test_task_will_execute()
    {
        TaskCompletionSource tcs = new TaskCompletionSource();
        await using BackgroundTaskScheduler scheduler = new BackgroundTaskScheduler(_blockProcessor, 1, LimboLogs.Instance);

        scheduler.ScheduleTask(1, (_, token) =>
        {
            tcs.SetResult(1);
            return Task.CompletedTask;
        });

        await tcs.Task;
    }

    [Test]
    public async Task Test_task_will_execute_concurrently_when_configured_so()
    {
        await using BackgroundTaskScheduler scheduler = new BackgroundTaskScheduler(_blockProcessor, 2, LimboLogs.Instance);

        int counter = 0;

        ManualResetEvent waitSignal = new ManualResetEvent(false);
        scheduler.ScheduleTask(1, async (_, token) =>
        {
            counter++;
            await waitSignal.WaitOneAsync(token);
            counter--;
        });
        scheduler.ScheduleTask(1, async (_, token) =>
        {
            counter++;
            await waitSignal.WaitOneAsync(token);
            counter--;
        });

        Assert.That(() => counter, Is.EqualTo(2).After(100, 1));
        waitSignal.Set();
    }

    [Test]
    public async Task Test_task_will_cancel_on_block_processing()
    {
        await using BackgroundTaskScheduler scheduler = new BackgroundTaskScheduler(_blockProcessor, 2, LimboLogs.Instance);

        bool wasCancelled = false;

        ManualResetEvent waitSignal = new ManualResetEvent(false);
        scheduler.ScheduleTask(1, async (_, token) =>
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
        _blockProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));
        Assert.That(() => wasCancelled, Is.EqualTo(true).After(10, 1));
    }

    [Test]
    public async Task Test_task_that_is_scheduled_during_block_processing_will_continue_after()
    {
        await using BackgroundTaskScheduler scheduler = new BackgroundTaskScheduler(_blockProcessor, 2, LimboLogs.Instance);
        _blockProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        int executionCount = 0;
        for (int i = 0; i < 5; i++)
        {
            scheduler.ScheduleTask(1, (_, token) =>
            {
                executionCount++;
                return Task.CompletedTask;
            });
        }

        await Task.Delay(10);
        executionCount.Should().Be(0);

        _blockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));
        Assert.That(() => executionCount, Is.EqualTo(5).After(10, 1));
    }

    [Test]
    public async Task Test_task_that_is_scheduled_during_block_processing_but_deadlined_will_get_called_and_cancelled()
    {
        await using BackgroundTaskScheduler scheduler = new BackgroundTaskScheduler(_blockProcessor, 2, LimboLogs.Instance);
        _blockProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        bool wasCancelled = false;
        ManualResetEvent waitSignal = new ManualResetEvent(false);
        scheduler.ScheduleTask(1, (_, token) =>
        {
            wasCancelled = token.IsCancellationRequested;
            waitSignal.Set();
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(1));

        (await waitSignal.WaitOneAsync(CancellationToken.None)).Should().BeTrue();

        wasCancelled.Should().BeTrue();
    }
}
