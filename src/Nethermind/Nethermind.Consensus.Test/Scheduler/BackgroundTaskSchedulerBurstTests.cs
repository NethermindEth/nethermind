// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Scheduler;

/// <summary>
/// Regression tests for the burst bug: during block processing, tasks must be disposed (not executed)
/// and the queue must remain healthy — no drops, no monotonic _queueCount growth.
/// After block processing ends, new tasks should execute normally.
/// </summary>
public class BackgroundTaskSchedulerBurstTests
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

    [TestCase(256, 5, 64, TestName = "Moderate_sustained_load")]
    [TestCase(512, 3, 200, TestName = "Heavy_receipt_serving_load")]
    [TestCase(2048, 10, 50, TestName = "Rapid_block_cycles")]
    [TestCase(128, 5, 50, TestName = "Small_capacity_mixed_producers")]
    public async Task Queue_should_not_drop_tasks_under_sustained_load(
        int capacity, int cycles, int tasksPerCycle)
    {
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 2, capacity, LimboLogs.Instance);

        int totalDropped = 0;
        int totalScheduled = 0;

        for (int cycle = 0; cycle < cycles; cycle++)
        {
            // Tasks scheduled during block processing are disposed, not executed
            _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

            for (int i = 0; i < tasksPerCycle; i++)
            {
                bool accepted = scheduler.TryScheduleTask(i, (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(5));

                if (accepted)
                    Interlocked.Increment(ref totalScheduled);
                else
                    Interlocked.Increment(ref totalDropped);
            }

            // Let disposed tasks drain
            await Task.Delay(150);
            _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));
            await Task.Delay(100);
        }

        totalDropped.Should().Be(0, $"no tasks should be dropped — {totalDropped} of {totalScheduled + totalDropped} were dropped");
        totalScheduled.Should().Be(cycles * tasksPerCycle);

        // After all block processing cycles, new tasks should execute normally
        int postCycleExecuted = 0;
        int postCycleCount = Math.Min(capacity, 100);
        for (int i = 0; i < postCycleCount; i++)
        {
            scheduler.TryScheduleTask(i, (_, _) =>
            {
                Interlocked.Increment(ref postCycleExecuted);
                return Task.CompletedTask;
            }).Should().BeTrue($"post-cycle task {i} should be accepted");
        }

        Assert.That(
            () => Volatile.Read(ref postCycleExecuted),
            Is.EqualTo(postCycleCount).After(5000, 10),
            "all post-cycle tasks should execute after block processing ends");
    }
}
