// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
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
/// Tests that expose the background task scheduler burst problem.
///
/// The core issue: the scheduler uses a single shared queue with only 2 workers
/// for ALL background tasks (tx processing, P2P serving of headers/bodies/receipts,
/// snap sync serving, history pruning). During block processing, tasks with remaining
/// deadline are re-queued and throttled. When block processing ends, all held-back tasks
/// burst through simultaneously. Heavy tasks (like receipt serving with DB reads) block
/// the pipeline and starve lightweight tasks (like tx processing).
///
/// This is especially severe during Old Receipts sync when peers aggressively request
/// receipts, but also happens during normal operation every 12 seconds.
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

    /// <summary>
    /// Simulates the Old Receipts sync scenario: peers continuously send GetReceipts
    /// requests (heavy tasks) while blocks are being processed every ~12 seconds.
    /// The queue should not overflow and tasks should not be dropped.
    ///
    /// The test runs multiple block processing cycles with continuous heavy task
    /// production (simulating peer receipt requests) and verifies no tasks are dropped.
    /// </summary>
    [Test]
    public async Task Continuous_heavy_load_across_block_processing_cycles_should_not_drop_tasks()
    {
        int capacity = 256;
        int concurrency = 2;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, concurrency, capacity, LimboLogs.Instance);

        int totalScheduled = 0;
        int totalExecuted = 0;
        int totalDropped = 0;
        int blockCycles = 5;

        // Simulate continuous receipt-serving load across multiple block processing cycles
        for (int cycle = 0; cycle < blockCycles; cycle++)
        {
            // Block processing starts
            _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

            // During block processing, peers keep sending requests.
            // Each "receipt serve" task takes 10ms of simulated DB work.
            int tasksPerCycle = capacity / 4;
            for (int i = 0; i < tasksPerCycle; i++)
            {
                bool accepted = scheduler.TryScheduleTask(i, async (_, token) =>
                {
                    // Simulate DB read for receipts (~10ms)
                    if (!token.IsCancellationRequested)
                    {
                        await Task.Delay(10, CancellationToken.None);
                    }
                    Interlocked.Increment(ref totalExecuted);
                }, TimeSpan.FromSeconds(5));

                if (accepted)
                    Interlocked.Increment(ref totalScheduled);
                else
                    Interlocked.Increment(ref totalDropped);
            }

            // Block processing takes ~150ms
            await Task.Delay(150);

            // Block processing ends
            _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

            // Brief gap before next block (simulates the remaining time in 12s slot)
            // In the test, keep it short. More tasks arrive in between blocks too.
            int interBlockTasks = tasksPerCycle / 2;
            for (int i = 0; i < interBlockTasks; i++)
            {
                bool accepted = scheduler.TryScheduleTask(i, async (_, token) =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        await Task.Delay(10, CancellationToken.None);
                    }
                    Interlocked.Increment(ref totalExecuted);
                }, TimeSpan.FromSeconds(5));

                if (accepted)
                    Interlocked.Increment(ref totalScheduled);
                else
                    Interlocked.Increment(ref totalDropped);
            }

            // Wait for tasks to drain before next cycle
            await Task.Delay(200);
        }

        // Wait for all remaining tasks to finish
        SpinWait spin = default;
        Stopwatch drainTimer = Stopwatch.StartNew();
        while (Volatile.Read(ref totalExecuted) < Volatile.Read(ref totalScheduled) && drainTimer.Elapsed < TimeSpan.FromSeconds(30))
        {
            spin.SpinOnce();
            if (spin.Count % 100 == 0)
                await Task.Yield();
        }

        totalDropped.Should().Be(0,
            $"no tasks should be dropped during sustained load — {totalDropped} of {totalScheduled + totalDropped} were dropped. " +
            "This indicates the queue is overflowing because tasks aren't draining fast enough during block processing cycles.");
        totalExecuted.Should().Be(totalScheduled,
            $"all scheduled tasks should eventually execute — {totalExecuted} of {totalScheduled} executed");
    }

    /// <summary>
    /// The most severe scenario: sustained heavy receipt-serving load with realistic
    /// task durations. With peers requesting old receipts, each serve task does substantial
    /// DB work. The queue fills up because 2 workers can't keep up with the incoming rate
    /// during block processing pauses.
    ///
    /// This test uses a higher incoming rate and longer task durations to simulate the
    /// actual Old Receipts sync pressure.
    /// </summary>
    [Test]
    public async Task Queue_should_not_overflow_under_sustained_receipt_serving_load()
    {
        int capacity = 512;
        int concurrency = 2;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, concurrency, capacity, LimboLogs.Instance);

        int totalDropped = 0;
        int totalScheduled = 0;
        int totalExecuted = 0;

        // Simulate 3 block processing cycles with aggressive receipt serving
        for (int cycle = 0; cycle < 3; cycle++)
        {
            _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

            // Aggressive incoming rate: tasks arriving faster than workers can drain them
            // This simulates multiple peers requesting receipts simultaneously
            for (int i = 0; i < 200; i++)
            {
                bool accepted = scheduler.TryScheduleTask(i, async (_, token) =>
                {
                    // Simulate receipt DB read: 20-50ms of actual work
                    if (!token.IsCancellationRequested)
                    {
                        await Task.Delay(20, CancellationToken.None);
                    }
                    Interlocked.Increment(ref totalExecuted);
                }, TimeSpan.FromSeconds(5));

                if (accepted)
                    Interlocked.Increment(ref totalScheduled);
                else
                    Interlocked.Increment(ref totalDropped);

                // Tasks arrive with small intervals (simulates network message timing)
                if (i % 10 == 0)
                    await Task.Delay(1);
            }

            // Block processing takes 200ms
            await Task.Delay(200);
            _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

            // Allow some draining between cycles
            await Task.Delay(300);
        }

        // Wait for completion
        SpinWait spin = default;
        Stopwatch drainTimer = Stopwatch.StartNew();
        while (Volatile.Read(ref totalExecuted) < Volatile.Read(ref totalScheduled) && drainTimer.Elapsed < TimeSpan.FromSeconds(30))
        {
            spin.SpinOnce();
            if (spin.Count % 100 == 0)
                await Task.Yield();
        }

        totalDropped.Should().Be(0,
            $"{totalDropped} tasks were dropped out of {totalScheduled + totalDropped} attempted. " +
            "Under sustained receipt-serving load, the queue overflows because heavy tasks aren't " +
            "draining during block processing and then burst through afterward.");
    }

    /// <summary>
    /// Simulates the exact real-world pattern: blocks arrive every ~2 seconds (compressed
    /// for test), each with a burst of held-back tasks. Tests that after multiple rapid
    /// block processing cycles, the queue depth remains bounded and doesn't grow unbounded.
    ///
    /// If the queue depth keeps growing across cycles, it means tasks from one cycle's burst
    /// haven't drained before the next cycle starts — the fundamental instability.
    /// </summary>
    [Test]
    public async Task Queue_depth_should_remain_bounded_across_rapid_block_processing_cycles()
    {
        int capacity = 2048;
        int concurrency = 2;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, concurrency, capacity, LimboLogs.Instance);

        int totalDropped = 0;
        int totalScheduled = 0;
        int totalExecuted = 0;
        int cycles = 10;
        int tasksPerCycle = 50;

        ConcurrentBag<int> queueDepthSamples = new();

        for (int cycle = 0; cycle < cycles; cycle++)
        {
            // Block processing starts — tasks get held
            _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

            // Tasks arrive during block processing
            for (int i = 0; i < tasksPerCycle; i++)
            {
                bool accepted = scheduler.TryScheduleTask(i, async (_, token) =>
                {
                    // Simulate moderate work
                    if (!token.IsCancellationRequested)
                    {
                        await Task.Delay(15, CancellationToken.None);
                    }
                    Interlocked.Increment(ref totalExecuted);
                }, TimeSpan.FromSeconds(10));

                if (accepted)
                    Interlocked.Increment(ref totalScheduled);
                else
                    Interlocked.Increment(ref totalDropped);
            }

            // Block processing takes 100ms
            await Task.Delay(100);

            // Sample queue depth just before releasing
            int pending = Volatile.Read(ref totalScheduled) - Volatile.Read(ref totalExecuted);
            queueDepthSamples.Add(pending);

            // Block processing ends — burst
            _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

            // Short gap between blocks (200ms compressed from 12s)
            await Task.Delay(200);
        }

        // Wait for final drain
        SpinWait spin = default;
        Stopwatch drainTimer = Stopwatch.StartNew();
        while (Volatile.Read(ref totalExecuted) < Volatile.Read(ref totalScheduled) && drainTimer.Elapsed < TimeSpan.FromSeconds(30))
        {
            spin.SpinOnce();
            if (spin.Count % 100 == 0)
                await Task.Yield();
        }

        int[] depths = queueDepthSamples.ToArray();
        Array.Sort(depths);

        totalDropped.Should().Be(0,
            $"{totalDropped} tasks dropped across {cycles} cycles");

        // The queue depth at each cycle's block-processing-end should be bounded.
        // If tasks from the previous cycle haven't drained, depth grows each cycle.
        int maxDepth = depths[^1];
        int lastDepth = depths[^1];

        // Max pending should not exceed 2 cycles' worth of tasks.
        // If it does, bursts are accumulating faster than they drain.
        maxDepth.Should().BeLessThan(tasksPerCycle * 2,
            $"max pending queue depth was {maxDepth} across {cycles} cycles — " +
            "tasks from previous block cycles are not draining before new cycles start, " +
            "creating unbounded backlog growth");
    }

    /// <summary>
    /// Tests that saturating the queue with tasks during Old Bodies/Receipts sync
    /// (where IsSyncing may be false because the node is at chain tip but still serving
    /// historical data) does not cause task drops that cascade into worse performance.
    ///
    /// The key pattern: queue fills → tasks dropped → peers retry → more tasks arrive →
    /// queue fills again → this creates a feedback loop. The scheduler should gracefully
    /// handle overload without entering this amplification cycle.
    /// </summary>
    [Test]
    public async Task Overloaded_queue_should_not_cause_cascading_task_drop_amplification()
    {
        int capacity = 128; // Small capacity to trigger overflow faster
        int concurrency = 2;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, concurrency, capacity, LimboLogs.Instance);

        int totalDropped = 0;
        int totalScheduled = 0;
        int totalExecuted = 0;

        // Run 5 block cycles with heavy sustained load that exceeds capacity
        for (int cycle = 0; cycle < 5; cycle++)
        {
            int droppedThisCycle = 0;

            _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

            // Burst of tasks from multiple producers simultaneously:
            // - TxPool: 20 tx messages
            // - P2P Bodies serving: 10 requests
            // - P2P Receipts serving: 10 requests
            // - P2P Headers serving: 5 requests
            // - Snap sync serving: 5 requests
            // Total: 50 tasks per cycle, some heavy, some light
            for (int i = 0; i < 50; i++)
            {
                int taskIndex = i;
                bool accepted = scheduler.TryScheduleTask(i, async (_, token) =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        // Vary work duration by "producer type"
                        int workMs = taskIndex switch
                        {
                            < 20 => 2,   // TxPool — lightweight
                            < 30 => 30,  // Bodies — moderate DB read
                            < 40 => 50,  // Receipts — heavy DB read
                            < 45 => 10,  // Headers — moderate
                            _ => 20      // Snap sync — moderate
                        };
                        await Task.Delay(workMs, CancellationToken.None);
                    }
                    Interlocked.Increment(ref totalExecuted);
                }, TimeSpan.FromSeconds(5));

                if (accepted)
                    Interlocked.Increment(ref totalScheduled);
                else
                {
                    Interlocked.Increment(ref totalDropped);
                    droppedThisCycle++;
                }
            }

            await Task.Delay(150); // Block processing time
            _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

            // The drop count per cycle should NOT increase over time.
            // If it does, it means previous drops created a feedback loop where
            // the queue never recovers between cycles.
            await Task.Delay(300); // Gap between blocks
        }

        // Wait for completion
        SpinWait spin = default;
        Stopwatch drainTimer = Stopwatch.StartNew();
        while (Volatile.Read(ref totalExecuted) < Volatile.Read(ref totalScheduled)
               && drainTimer.Elapsed < TimeSpan.FromSeconds(30))
        {
            spin.SpinOnce();
            if (spin.Count % 100 == 0)
                await Task.Yield();
        }

        // No tasks should be dropped at all with proper scheduling.
        // 50 tasks per cycle * 5 cycles = 250 total, capacity is 128.
        // With proper draining during and between block processing, the queue
        // should handle this without overflow.
        totalDropped.Should().Be(0,
            $"{totalDropped} tasks were dropped across 5 cycles with mixed producers. " +
            "The scheduler cannot handle the realistic mix of txpool + P2P serving + " +
            "sync serving tasks without overflow, causing cascading degradation.");
    }

    /// <summary>
    /// The scheduler has no priority mechanism. All tasks — whether urgent TxPool validation
    /// or bulk historical receipt serving — share the same FIFO queue. When the queue is full
    /// of slow tasks, urgent tasks are simply rejected.
    ///
    /// In production: receipt sync fills the queue with 50-100ms DB read tasks. TxPool needs
    /// to schedule tx validation (sub-ms). The TxPool task is rejected, causing transaction
    /// processing to stall, which degrades block building and mempool quality.
    /// </summary>
    [Test]
    public async Task No_priority_mechanism_allows_bulk_tasks_to_block_urgent_ones()
    {
        int capacity = 128;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 2, capacity, LimboLogs.Instance);

        // Hold tasks in the queue
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        // Fill queue with slow, non-urgent receipt serving tasks
        for (int i = 0; i < capacity; i++)
        {
            scheduler.TryScheduleTask(i, async (_, token) =>
            {
                await Task.Delay(50, CancellationToken.None);
            }, TimeSpan.FromSeconds(30));
        }

        // Urgent TxPool task arrives — should be accepted with high priority,
        // potentially evicting a lower-priority task
        bool urgentAccepted = scheduler.TryScheduleTask(0, (_, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(100));

        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

        urgentAccepted.Should().BeTrue(
            "urgent tasks (like TxPool validation) should be accepted even when the queue is " +
            "full of low-priority bulk tasks — the scheduler has no priority mechanism, so one " +
            "producer type monopolizing the queue blocks ALL other producer types completely. " +
            "In production, this prevents tx processing during receipt sync.");
    }

    /// <summary>
    /// The scheduler shares a single global queue capacity across ALL producer types.
    /// When one producer floods the queue (e.g., receipt serving during Old Receipts sync),
    /// other producers' task drops are determined purely by arrival order — not by producer
    /// importance or fair allocation. This means later-arriving producers bear all the cost.
    ///
    /// In production: receipt serving fills 80%+ of the queue → TxPool, snap sync, and
    /// pruning producers all get rejected → cascading degradation across ALL subsystems.
    /// </summary>
    [Test]
    public async Task Queue_capacity_is_global_not_per_producer_causing_unfair_drops()
    {
        int capacity = 64;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 2, capacity, LimboLogs.Instance);

        // Hold tasks in the queue by activating block processing
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        // 4 producer types each try to schedule 20 tasks (total 80, capacity 64)
        string[] producerNames = ["TxPool", "ReceiptServing", "BodyServing", "SnapSync"];
        int[] producerDrops = new int[4];

        for (int producer = 0; producer < 4; producer++)
        {
            int p = producer;
            for (int i = 0; i < 20; i++)
            {
                bool accepted = scheduler.TryScheduleTask(
                    producer * 20 + i, (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(10));
                if (!accepted) producerDrops[p]++;
            }
        }

        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

        int maxDrops = 0;
        int minDrops = int.MaxValue;
        for (int p = 0; p < 4; p++)
        {
            maxDrops = Math.Max(maxDrops, producerDrops[p]);
            minDrops = Math.Min(minDrops, producerDrops[p]);
        }

        // Fair distribution: 16 total drops spread evenly = 4 drops per producer (max-min ≤ 1).
        // Current: all 16 drops go to the last producer(s) because the queue is first-come-first-served.
        (maxDrops - minDrops).Should().BeLessOrEqualTo(1,
            $"drops are unfairly distributed across producers: " +
            $"{producerNames[0]}={producerDrops[0]}, {producerNames[1]}={producerDrops[1]}, " +
            $"{producerNames[2]}={producerDrops[2]}, {producerNames[3]}={producerDrops[3]} — " +
            "the scheduler has no per-producer fairness, causing later producers to be " +
            "completely starved when the queue approaches capacity");
    }

    /// <summary>
    /// The queue doesn't need to reach 2048 capacity to cause problems. Even at 50% capacity,
    /// ALL non-expired tasks are held back during block processing. The more tasks in the queue
    /// when block processing starts, the more tasks are blocked, the more the post-processing
    /// burst grows, and the more expired-task-drain overhead occurs.
    ///
    /// This test demonstrates graduated degradation: queue at 25%, 50%, 75% capacity all
    /// cause proportionally worse recovery times after block processing. There's no cliff at
    /// 2048 — the problem is continuous and proportional to queue depth.
    /// </summary>
    [Test]
    public async Task Graduated_queue_pressure_degrades_recovery_proportional_to_depth()
    {
        int capacity = 2048;
        long[] recoveryTimesMs = new long[3];
        int[] fillLevels = [capacity / 4, capacity / 2, capacity * 3 / 4]; // 25%, 50%, 75%

        for (int level = 0; level < fillLevels.Length; level++)
        {
            IBranchProcessor bp = Substitute.For<IBranchProcessor>();
            IChainHeadInfoProvider chi = Substitute.For<IChainHeadInfoProvider>();
            chi.IsSyncing.Returns(false);
            await using BackgroundTaskScheduler scheduler = new(bp, chi, 2, capacity, LimboLogs.Instance);

            int fillCount = fillLevels[level];
            int completed = 0;

            // Fill queue to the target level with moderate-work tasks
            for (int i = 0; i < fillCount; i++)
            {
                scheduler.TryScheduleTask(i, async (_, token) =>
                {
                    if (!token.IsCancellationRequested)
                        await Task.Delay(10, CancellationToken.None);
                    Interlocked.Increment(ref completed);
                }, TimeSpan.FromSeconds(10));
            }

            // Let some tasks start processing
            await Task.Delay(30);

            // Block processing starts — holds back all remaining tasks
            bp.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));
            await Task.Delay(100);

            // Block processing ends — burst of held-back tasks
            bp.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

            // Measure recovery time
            Stopwatch recovery = Stopwatch.StartNew();
            SpinWait spin = default;
            while (Volatile.Read(ref completed) < fillCount && recovery.Elapsed < TimeSpan.FromSeconds(30))
            {
                spin.SpinOnce();
                if (spin.Count % 100 == 0)
                    await Task.Yield();
            }
            recovery.Stop();
            recoveryTimesMs[level] = recovery.ElapsedMilliseconds;
        }

        // Recovery time should be roughly proportional to queue depth.
        // The IDEAL behavior: recovery time is bounded regardless of queue depth
        // (e.g., use work-stealing, parallel drain, or don't hold tasks at all).
        //
        // For this test to PASS, recovery at 75% should be at most 2x recovery at 25%.
        // Currently, recovery scales linearly because the burst is proportional to queue depth.
        long recoveryAt25 = recoveryTimesMs[0];
        long recoveryAt75 = recoveryTimesMs[2];

        recoveryAt75.Should().BeLessThan(recoveryAt25 * 2 + 100,
            $"recovery time scales with queue depth: 25%={recoveryTimesMs[0]}ms, " +
            $"50%={recoveryTimesMs[1]}ms, 75%={recoveryTimesMs[2]}ms — " +
            "the scheduler's hold-and-release-all pattern means recovery degrades linearly " +
            "with queue depth. Even at 50% capacity (1024 tasks), the post-block burst is " +
            "significant. The 2048 overflow is just the visible symptom of a continuous problem.");
    }

    /// <summary>
    /// The scheduler has no eviction mechanism. When the queue fills with stale tasks
    /// (tasks that were scheduled a long time ago but have long deadlines), there's no
    /// way to remove them to make room for fresh, more relevant tasks.
    ///
    /// In production: during receipt sync, receipt-serving tasks with 30-second deadlines
    /// fill the queue. If the actual responses aren't needed anymore (peer disconnected,
    /// sync completed for that range), these stale tasks still occupy queue slots. New
    /// incoming tasks from TxPool, snap sync, or other producers are rejected.
    ///
    /// The only way queue slots free up is: task expires naturally, or block processing
    /// drains expired tasks. There's no forced eviction or replacement policy.
    /// </summary>
    [Test]
    public async Task Scheduler_has_no_eviction_mechanism_for_stale_tasks()
    {
        int capacity = 64;
        await using BackgroundTaskScheduler scheduler = new(_branchProcessor, _chainHeadInfo, 2, capacity, LimboLogs.Instance);

        // Hold tasks in queue during block processing
        _branchProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(null));

        // Fill queue with tasks that have LONG deadline (30 seconds)
        for (int i = 0; i < capacity; i++)
        {
            scheduler.TryScheduleTask(i, (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(30));
        }

        // Wait 1 second — tasks are "stale" but won't expire for 29 more seconds
        await Task.Delay(1000);

        // Try to schedule a fresh, more relevant task — rejected because of stale tasks
        bool freshAccepted = scheduler.TryScheduleTask(0, (_, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(2));

        _branchProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(null, null));

        // An ideal scheduler would evict the oldest/stalest tasks (or those closest to expiry)
        // to make room for fresh tasks. Currently, the queue is a fixed-size bucket with no
        // replacement policy — first-come-first-served, regardless of staleness.
        freshAccepted.Should().BeTrue(
            "fresh tasks are rejected when the queue is full of stale 30-second-old tasks — " +
            "the scheduler has no eviction mechanism to replace stale tasks with fresh ones. " +
            "In production, this means peer disconnections or sync completion don't free queue " +
            "slots; the stale tasks sit for their full timeout before slots become available.");
    }
}
