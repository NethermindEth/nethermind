// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.BlockProfiler;

/// <summary>
/// Pass-through <see cref="IBranchProcessor"/> decorator that logs, for every block, the user-space instructions and
/// cycles the processing thread retired and the bytes it allocated, split into transaction execution, roots and
/// commit. Enabled by <c>NETHERMIND_COUNT_INSTRUCTIONS=1</c>.
/// </summary>
/// <remarks>
/// <para>
/// A block's window runs from <see cref="IBranchProcessor.BlockProcessing"/> to <see cref="IBranchProcessor.BlockProcessed"/>:
/// transaction execution up to <see cref="IBlockProcessor.TransactionsExecuted"/>, the receipts and the state root up
/// to the verdict (<see cref="IBranchProcessor.BlockExecuted"/>), then the tree commit. Only the processing thread is
/// counted, so the counts repeat between runs only when nothing is handed to other threads: run with
/// <see cref="DeterministicBenchmark"/> on, <c>DOTNET_PROCESSOR_COUNT=1</c> and prewarming off.
/// </para>
/// <para>
/// <c>DOTNET_PROCESSOR_COUNT=1</c> also makes the runtime fall back to workstation GC, which collects on the thread that
/// allocates. Each branch therefore runs in a no-GC region entered before its window, with the scheduler's forced
/// collections held off; run with <c>--Init.DisableGcOnNewPayload=false</c> so the region is this decorator's alone.
/// The verdict is held until the branch completes, so the answered newPayload's forkchoiceUpdated cannot run beside
/// the commit.
/// </para>
/// <para>
/// <c>NETHERMIND_COUNT_PIN_CPU=n</c> keeps the processing thread on CPU n and the node's other threads off it, which
/// steadies the cycle counts; the instruction counts don't need it.
/// </para>
/// </remarks>
public sealed class CountingBranchProcessor : IBranchProcessor, IDisposable
{
    private static readonly int s_pinCpu = int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_COUNT_PIN_CPU"), out int cpu) ? cpu : -1;
    private static int s_armedLogged;
    private static int s_pinned;

    private readonly IBranchProcessor _inner;
    private readonly IBlockProcessor? _blockProcessor;
    private readonly ILogger _logger;
    private Window _block;
    private ThreadInstructionCounter.Sample _executed;
    private bool _executedRead;
    private ThreadInstructionCounter.Sample _judged;
    private bool _judgedRead;
    private GCScheduler.ForcedGCExclusionScope? _forcedGCExclusion;
    private EventHandler<BlockExecutedEventArgs>? _blockExecuted;
    private readonly List<BlockExecutedEventArgs> _pendingVerdicts = [];

    public CountingBranchProcessor(IBranchProcessor inner, ILogManager logManager, IBlockProcessor? blockProcessor = null)
    {
        _inner = inner;
        _blockProcessor = blockProcessor;
        _logger = logManager.GetClassLogger<CountingBranchProcessor>();
        _inner.BlocksProcessing += OnBlocksProcessing;
        _inner.BlockProcessing += OnBlockProcessing;
        _inner.BlockExecuted += OnInnerBlockExecuted;
        _inner.BlockProcessed += OnBlockProcessed;
        _inner.BranchProcessingCompleted += OnBranchProcessingCompleted;
        // Optional: without a block processor the block still counts, only without the execution split.
        if (_blockProcessor is not null) _blockProcessor.TransactionsExecuted += OnTransactionsExecuted;

        // Branch processors are scoped; report once per process whether the counters work.
        if (Interlocked.Exchange(ref s_armedLogged, 1) == 0)
        {
            if (ThreadInstructionCounter.TryRead(out _))
            {
                if (_logger.IsInfo) _logger.Info("EXPB-COUNT armed: user-space instructions and cycles per block");
            }
            else if (_logger.IsWarn)
            {
                _logger.Warn($"EXPB-COUNT unavailable: {ThreadInstructionCounter.Error}");
            }
        }
    }

    public Block[] Process(BlockHeader? baseBlock, IReadOnlyList<Block> suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer, CancellationToken token = default)
        => _inner.Process(baseBlock, suggestedBlocks, processingOptions, blockTracer, token);

    private void OnBlocksProcessing(object? sender, BlocksProcessingEventArgs e)
    {
        _forcedGCExclusion = GCScheduler.Instance.ExcludeForcedGC();
        if (s_pinCpu >= 0) PinProcessingThread();
        // The region's own collection runs here, before any block window opens.
        NoGcRegion.TryEnter(_logger);
    }

    private void PinProcessingThread()
    {
        bool pinned = ThreadAffinity.PinCurrentThread(s_pinCpu);
        // Threads started since the last branch inherited the full set, so move them off every time.
        int moved = pinned ? ThreadAffinity.ExcludeFromOtherThreads(s_pinCpu) : 0;
        if (Interlocked.Exchange(ref s_pinned, 1) == 0 && _logger.IsInfo)
        {
            _logger.Info(pinned
                ? $"EXPB-COUNT block processing kept on CPU {s_pinCpu}, {moved} other threads moved off it"
                : $"EXPB-COUNT could not pin block processing to CPU {s_pinCpu}");
        }
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        _executedRead = false;
        _judgedRead = false;
        _block = Window.Open();
    }

    private void OnTransactionsExecuted() =>
        _executedRead = _block.IsOnCurrentThread && ThreadInstructionCounter.TryRead(out _executed);

    private void OnInnerBlockExecuted(object? sender, BlockExecutedEventArgs e)
    {
        _judgedRead = _block.IsOnCurrentThread && ThreadInstructionCounter.TryRead(out _judged);
        _pendingVerdicts.Add(e);
    }

    private void OnBlockProcessed(object? sender, BlockProcessedEventArgs e)
    {
        if (!_block.TryStop(out ThreadInstructionCounter.Sample total, out string counts)) return;
        if (!_logger.IsInfo) return;

        ThreadInstructionCounter.Sample start = _block.Start;
        string split = _executedRead && _judgedRead
            ? $" exec={_executed.Instructions - start.Instructions}" +
              $" roots={_judged.Instructions - _executed.Instructions}" +
              $" commit={start.Instructions + total.Instructions - _judged.Instructions}" +
              $" cyc={_executed.Cycles - start.Cycles}/{_judged.Cycles - _executed.Cycles}/{start.Cycles + total.Cycles - _judged.Cycles}"
            : string.Empty;
        Block block = e.Block;
        _logger.Info($"EXPB-COUNT block={block.Number} txs={block.Transactions.Length} gas={block.GasUsed} {counts}{split}");
    }

    private void OnBranchProcessingCompleted(object? sender, BranchProcessingCompletedEventArgs e)
    {
        NoGcRegion.Exit();
        _forcedGCExclusion?.Dispose();
        _forcedGCExclusion = null;

        // Hands the held verdicts on, unless the branch failed after reaching them.
        bool succeeded = e.Exception is null && e.ProcessedBlocksCount == e.SuggestedBlocks.Count;
        if (succeeded)
        {
            foreach (BlockExecutedEventArgs verdict in _pendingVerdicts) _blockExecuted?.Invoke(this, verdict);
        }
        _pendingVerdicts.Clear();
    }

    /// <summary>Counter readings and runtime counts at a block window's start, on the thread that opened it.</summary>
    private readonly struct Window
    {
        private readonly long _allocated;
        private readonly int _gen0;
        private readonly int _gen1;
        private readonly int _gen2;
        private readonly int _threadId;
        private readonly long _jitMethods;

        private Window(ThreadInstructionCounter.Sample start)
        {
            Start = start;
            _allocated = GC.GetAllocatedBytesForCurrentThread();
            _gen0 = GC.CollectionCount(0);
            _gen1 = GC.CollectionCount(1);
            _gen2 = GC.CollectionCount(2);
            _threadId = Environment.CurrentManagedThreadId;
            _jitMethods = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
        }

        public ThreadInstructionCounter.Sample Start { get; }

        public static Window Open() => ThreadInstructionCounter.TryRead(out ThreadInstructionCounter.Sample sample) ? new Window(sample) : default;

        public bool IsOnCurrentThread => _threadId != 0 && _threadId == Environment.CurrentManagedThreadId;

        /// <summary>The window's deltas and their log fields; false when it never started or ends on another thread.</summary>
        public bool TryStop(out ThreadInstructionCounter.Sample total, out string counts)
        {
            total = default;
            counts = string.Empty;
            if (!IsOnCurrentThread || !ThreadInstructionCounter.TryRead(out ThreadInstructionCounter.Sample end)) return false;

            total = end - Start;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - _allocated;
            // gc: collections that ran inside the window (the no-GC region keeps them at 0); mux: whether the kernel
            // multiplexed the counters; jit: methods this thread compiled. Any of them non-zero makes the block's
            // counts unrepresentative.
            counts = $"instr={total.Instructions} cycles={total.Cycles} alloc={allocated} " +
                $"gc={GC.CollectionCount(0) - _gen0}/{GC.CollectionCount(1) - _gen1}/{GC.CollectionCount(2) - _gen2} " +
                $"mux={(total.Multiplexed ? 1 : 0)} jit={System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - _jitMethods}";
            return true;
        }
    }

    /// <summary>The no-GC region a branch runs in, sized down once if the GC mode refuses the first size.</summary>
    private static class NoGcRegion
    {
        // Nethermind's own newPayload region is 512 MB plus 64 MB of large objects.
        private static readonly long[] s_sizes = [576L << 20, 256L << 20, 128L << 20];
        private static int s_sizeIndex;
        private static bool s_entered;

        public static void TryEnter(ILogger logger)
        {
            s_entered = false;
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) return;
            while (s_sizeIndex < s_sizes.Length)
            {
                long size = s_sizes[s_sizeIndex];
                try
                {
                    s_entered = GC.TryStartNoGCRegion(size, Math.Min(size / 8, 64L << 20), disallowFullBlockingGC: true);
                    return;
                }
                catch (ArgumentOutOfRangeException)
                {
                    if (logger.IsWarn) logger.Warn($"EXPB-COUNT no-GC region of {size >> 20} MB refused, trying smaller");
                    s_sizeIndex++;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
        }

        public static void Exit()
        {
            if (!s_entered) return;
            s_entered = false;
            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion) return;
            try
            {
                GC.EndNoGCRegion();
            }
            catch (InvalidOperationException)
            {
                // A collection already ended the region.
            }
        }
    }

    public event EventHandler<BlockExecutedEventArgs>? BlockExecuted
    {
        add => _blockExecuted += value;
        remove => _blockExecuted -= value;
    }

    public event EventHandler<BlockProcessedEventArgs>? BlockProcessed
    {
        add => _inner.BlockProcessed += value;
        remove => _inner.BlockProcessed -= value;
    }

    public event EventHandler<BlocksProcessingEventArgs>? BlocksProcessing
    {
        add => _inner.BlocksProcessing += value;
        remove => _inner.BlocksProcessing -= value;
    }

    public event EventHandler<BlockEventArgs>? BlockProcessing
    {
        add => _inner.BlockProcessing += value;
        remove => _inner.BlockProcessing -= value;
    }

    public event EventHandler<BranchProcessingCompletedEventArgs>? BranchProcessingCompleted
    {
        add => _inner.BranchProcessingCompleted += value;
        remove => _inner.BranchProcessingCompleted -= value;
    }

    public void Dispose()
    {
        _inner.BlocksProcessing -= OnBlocksProcessing;
        _inner.BlockProcessing -= OnBlockProcessing;
        _inner.BlockExecuted -= OnInnerBlockExecuted;
        _inner.BlockProcessed -= OnBlockProcessed;
        _inner.BranchProcessingCompleted -= OnBranchProcessingCompleted;
        if (_blockProcessor is not null) _blockProcessor.TransactionsExecuted -= OnTransactionsExecuted;
        _forcedGCExclusion?.Dispose();
        // The container owns the decorated instance; disposing it here would double-dispose.
    }
}
