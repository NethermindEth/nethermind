// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
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
/// Regression tests for the re-queue/throttle burst bug: during block processing, unexpired tasks
/// were re-queued via goto Throttle, causing _queueCount to grow monotonically toward the capacity cap.
/// With the fix, tasks execute immediately with a cancelled CancellationToken during block processing.
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

    [TestCase(256, 5, 64, 10, TestName = "Moderate_sustained_load")]
    [TestCase(512, 3, 200, 20, TestName = "Heavy_receipt_serving_load")]
    [TestCase(2048, 10, 50, 15, TestName = "Rapid_block_cycles")]
    [TestCase(128, 5, 50, 2, TestName = "Small_capacity_mixed_producers")]
    public async Task Queue_should_not_drop_tasks_under_sustained_load(
        int capacity, int cycles, int tasksPerCycle, int taskDurationMs)
    {
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 2, capacity, LimboLogs.Instance);

        int totalDropped = 0;
        int totalScheduled = 0;
        int totalExecuted = 0;

        for (int cycle = 0; cycle < cycles; cycle++)
        {
            _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

            for (int i = 0; i < tasksPerCycle; i++)
            {
                bool accepted = scheduler.TryScheduleTask(i, async (_, token) =>
                {
                    if (!token.IsCancellationRequested)
                        await Task.Delay(taskDurationMs, CancellationToken.None);
                    Interlocked.Increment(ref totalExecuted);
                }, TimeSpan.FromSeconds(5));

                if (accepted)
                    Interlocked.Increment(ref totalScheduled);
                else
                    Interlocked.Increment(ref totalDropped);
            }

            await Task.Delay(150);
            _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));
            await Task.Delay(300);
        }

        SpinWait spin = default;
        Stopwatch drainTimer = Stopwatch.StartNew();
        while (Volatile.Read(ref totalExecuted) < Volatile.Read(ref totalScheduled)
               && drainTimer.Elapsed < TimeSpan.FromSeconds(30))
        {
            spin.SpinOnce();
            if (spin.Count % 100 == 0)
                await Task.Yield();
        }

        totalDropped.Should().Be(0, $"no tasks should be dropped — {totalDropped} of {totalScheduled + totalDropped} were dropped");
        totalExecuted.Should().Be(totalScheduled, $"all scheduled tasks should execute — {totalExecuted} of {totalScheduled}");
    }
}
