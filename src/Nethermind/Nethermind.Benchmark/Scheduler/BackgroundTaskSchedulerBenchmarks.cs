// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Benchmarks.Scheduler;

/// <summary>
/// Benchmarks the throughput of the BackgroundTaskScheduler under concurrent task
/// scheduling with periodic block-processing pauses — the scenario that caused
/// the "Background task queue is full" issue on synced nodes.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 5)]
public class BackgroundTaskSchedulerBenchmarks
{
    private StubBranchProcessor _branchProcessor = null!;
    private StubChainHeadInfoProvider _chainHeadInfo = null!;

    [Params(1024, 2048)]
    public int Capacity { get; set; }

    [Params(2)]
    public int Concurrency { get; set; }

    [Params(50)]
    public int BlockProcessingDurationMs { get; set; }

    [Params(5)]
    public int BlockProcessingCycles { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _branchProcessor = new StubBranchProcessor();
        _chainHeadInfo = new StubChainHeadInfoProvider();
    }

    /// <summary>
    /// Simulates the real-world scenario: a background producer keeps scheduling tasks
    /// while block-processing cycles pause and resume execution.  Measures total wall-clock
    /// time for scheduling + draining all tasks across several block-processing windows.
    /// </summary>
    [Benchmark]
    public async Task ScheduleAndDrainDuringBlockProcessing()
    {
        await using BackgroundTaskScheduler scheduler = new(
            _branchProcessor, _chainHeadInfo, Concurrency, Capacity, LimboLogs.Instance);

        int totalScheduled = 0;
        int totalExecuted = 0;
        int totalDropped = 0;

        for (int cycle = 0; cycle < BlockProcessingCycles; cycle++)
        {
            // Simulate block arriving — cancels current tasks, pauses non-expired ones
            _branchProcessor.RaiseBlocksProcessing();

            // Schedule a burst of tasks while block is being processed
            int batchSize = Capacity / 2;
            for (int i = 0; i < batchSize; i++)
            {
                bool accepted = scheduler.TryScheduleTask(i, (_, token) =>
                {
                    Interlocked.Increment(ref totalExecuted);
                    return Task.CompletedTask;
                }, TimeSpan.FromMilliseconds(BlockProcessingDurationMs + 100));

                if (accepted)
                    Interlocked.Increment(ref totalScheduled);
                else
                    Interlocked.Increment(ref totalDropped);
            }

            // Simulate block processing time
            await Task.Delay(BlockProcessingDurationMs);

            // Block done — resume normal task execution
            _branchProcessor.RaiseBlockProcessed();

            // Wait for all scheduled tasks to drain before next cycle
            SpinWait spin = default;
            while (Volatile.Read(ref totalExecuted) < Volatile.Read(ref totalScheduled))
            {
                spin.SpinOnce();
                if (spin.Count % 100 == 0)
                    await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Measures pure scheduling throughput without block-processing interruptions.
    /// Useful as a baseline to compare against <see cref="ScheduleAndDrainDuringBlockProcessing"/>.
    /// </summary>
    [Benchmark(Baseline = true)]
    public async Task ScheduleAndDrainWithoutBlockProcessing()
    {
        await using BackgroundTaskScheduler scheduler = new(
            _branchProcessor, _chainHeadInfo, Concurrency, Capacity, LimboLogs.Instance);

        int totalScheduled = 0;
        int totalExecuted = 0;

        int totalTasks = (Capacity / 2) * BlockProcessingCycles;
        for (int i = 0; i < totalTasks; i++)
        {
            bool accepted = scheduler.TryScheduleTask(i, (_, _) =>
            {
                Interlocked.Increment(ref totalExecuted);
                return Task.CompletedTask;
            });
            if (accepted)
                Interlocked.Increment(ref totalScheduled);
        }

        SpinWait spin = default;
        while (Volatile.Read(ref totalExecuted) < Volatile.Read(ref totalScheduled))
        {
            spin.SpinOnce();
            if (spin.Count % 100 == 0)
                await Task.Yield();
        }
    }

    /// <summary>
    /// Minimal stub for <see cref="IBranchProcessor"/> to expose events without any real block processing.
    /// </summary>
    private sealed class StubBranchProcessor : IBranchProcessor
    {
        public event EventHandler<BlockProcessedEventArgs>? BlockProcessed;
        public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing;
#pragma warning disable CS0067 // Event is never used
        public event EventHandler<BlockEventArgs>? BlockProcessing;
#pragma warning restore CS0067

        public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks,
            ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token = default)
            => [];

        public void RaiseBlocksProcessing() =>
            BlocksProcessing?.Invoke(this, new BlocksProcessingEventArgs([]));

        public void RaiseBlockProcessed() =>
            BlockProcessed?.Invoke(this, new BlockProcessedEventArgs(null!, null!));
    }

    /// <summary>
    /// Minimal stub for <see cref="IChainHeadInfoProvider"/> — reports node as not syncing.
    /// </summary>
    private sealed class StubChainHeadInfoProvider : IChainHeadInfoProvider
    {
        public IChainHeadSpecProvider SpecProvider => null!;
        public IReadOnlyStateProvider ReadOnlyStateProvider => null!;
        public long HeadNumber => 0;
        public long? BlockGasLimit => null;
        public UInt256 CurrentBaseFee => UInt256.Zero;
        public UInt256 CurrentFeePerBlobGas => UInt256.Zero;
        public ProofVersion CurrentProofVersion => ProofVersion.V0;
        public bool IsSyncing => false;
        public bool IsProcessingBlock => false;
#pragma warning disable CS0067 // Event is never used
        public event EventHandler<BlockReplacementEventArgs>? HeadChanged;
#pragma warning restore CS0067
    }
}
