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
using FlatMetrics = Nethermind.State.Flat.Metrics;

namespace Nethermind.BlockProfiler;

/// <summary>
/// Pass-through <see cref="IBranchProcessor"/> decorator that logs, for every block and every branch, the
/// user-space instructions and cycles the processing thread retired, the bytes it allocated and the collections
/// that ran meanwhile. Enabled by <c>NETHERMIND_COUNT_INSTRUCTIONS=1</c>.
/// </summary>
/// <remarks>
/// The block window runs from <see cref="IBranchProcessor.BlockProcessing"/> to
/// <see cref="IBranchProcessor.BlockProcessed"/>: execution, the roots and the tree commit. The branch window runs
/// from <see cref="IBranchProcessor.BlocksProcessing"/> to <see cref="IBranchProcessor.BranchProcessingCompleted"/>
/// and adds the state scope's commit on close. Both are counted on the processing thread only, so work handed to
/// the thread pool is outside them; the instruction count repeats only when that work is kept inline
/// (<c>DOTNET_PROCESSOR_COUNT=1</c>, prewarming off, <c>NETHERMIND_NO_EARLY_SENDER_RECOVERY=1</c>).
/// These handlers subscribe first, so the other subscribers' start handlers are inside the windows and their
/// end handlers outside. A block line also splits its window at <see cref="IBlockProcessor.TransactionsExecuted"/>:
/// <c>exec</c> is transaction execution, <c>post</c> the receipts, roots and commit after it.
/// <para>
/// <c>DOTNET_PROCESSOR_COUNT=1</c> also makes the runtime fall back to workstation GC, which collects on the thread
/// that allocates, so a collection landing in a window would be counted with the block. Each branch therefore
/// runs in a no-GC region entered before its window opens, with the scheduler's forced collections held off,
/// under <c>--Init.DisableGcOnNewPayload=false</c> so the region is this decorator's alone.
/// </para>
/// </remarks>
public sealed class CountingBranchProcessor : IBranchProcessor, IDisposable
{
    private static int s_armedLogged;

    /// <summary>
    /// <c>NETHERMIND_COUNT_DEFER_VERDICT=1</c> holds <see cref="BlockExecuted"/> until the branch window closes, so the
    /// answered newPayload's forkchoiceUpdated cannot run alongside the window's commit.
    /// </summary>
    private static readonly bool s_deferVerdict = Environment.GetEnvironmentVariable("NETHERMIND_COUNT_DEFER_VERDICT") == "1";

    private readonly IBranchProcessor _inner;
    private readonly IBlockProcessor? _blockProcessor;
    private readonly ILogger _logger;
    private Window _branch;
    private Window _block;
    private ThreadInstructionCounter.Sample _executed;
    private bool _executedRead;
    private ThreadInstructionCounter.Sample _judged;
    private bool _judgedRead;
    private GCScheduler.ForcedGCExclusionScope? _forcedGCExclusion;
    private EventHandler<BlockExecutedEventArgs>? _deferredBlockExecuted;
    private BlockExecutedEventArgs? _pendingVerdict;

    public CountingBranchProcessor(IBranchProcessor inner, ILogManager logManager, IBlockProcessor? blockProcessor = null)
    {
        _inner = inner;
        _blockProcessor = blockProcessor;
        _logger = logManager.GetClassLogger<CountingBranchProcessor>();
        _inner.BlocksProcessing += OnBlocksProcessing;
        _inner.BlockProcessing += OnBlockProcessing;
        _inner.BlockProcessed += OnBlockProcessed;
        _inner.BranchProcessingCompleted += OnBranchProcessingCompleted;
        // Optional: a scope without a block processor still counts, only without the execution split.
        if (_blockProcessor is not null) _blockProcessor.TransactionsExecuted += OnTransactionsExecuted;
        // Always observed: the verdict splits the post-execution work into roots and commit.
        _inner.BlockExecuted += OnInnerBlockExecuted;
        // Branch processors are scoped; report whether the counters work once per process.
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
        // The region's own collection runs here, before the window opens.
        NoGcRegion.TryEnter(_logger);
        _branch = Window.Start();
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        _executedRead = false;
        _judgedRead = false;
        _block = Window.Start();
    }

    // Splits the block window: transaction execution before this point, receipts, roots and commit after it.
    private void OnTransactionsExecuted() =>
        _executedRead = _block.IsOnCurrentThread && ThreadInstructionCounter.TryRead(out _executed);

    private void OnBlockProcessed(object? sender, BlockProcessedEventArgs e)
    {
        if (!_block.TryStop(out string counts, out ulong instructions)) return;
        ulong executed = _executedRead ? _executed.Instructions - _block.StartInstructions : 0;
        // roots: receipts, blooms and the state root up to the verdict; commit: the tree commit after it.
        ulong roots = _judgedRead && _executedRead ? _judged.Instructions - _executed.Instructions : 0;
        ulong commit = _judgedRead ? _block.StartInstructions + instructions - _judged.Instructions : 0;
        Block block = e.Block;
        if (_logger.IsInfo) _logger.Info($"EXPB-COUNT block={block.Number} txs={block.Transactions.Length} gas={block.GasUsed} {counts} exec={executed} post={instructions - executed} roots={roots} commit={commit}");
    }

    private void OnInnerBlockExecuted(object? sender, BlockExecutedEventArgs e)
    {
        _judgedRead = _block.IsOnCurrentThread && ThreadInstructionCounter.TryRead(out _judged);
        if (s_deferVerdict) _pendingVerdict = e;
    }

    private void OnBranchProcessingCompleted(object? sender, BranchProcessingCompletedEventArgs e)
    {
        bool stopped = _branch.TryStop(out string counts, out _);
        bool regionHeld = NoGcRegion.Exit();
        _forcedGCExclusion?.Dispose();
        _forcedGCExclusion = null;
        ReleaseVerdict(e.Exception is null && e.ProcessedBlocksCount == e.SuggestedBlocks.Count);
        if (!stopped || e.SuggestedBlocks.Count == 0) return;
        if (_logger.IsInfo) _logger.Info($"EXPB-COUNT branch={e.SuggestedBlocks[0].Number} blocks={e.ProcessedBlocksCount} {counts} nogc={(regionHeld ? 1 : 0)}");
    }

    /// <summary>Hands a held verdict to the subscribers, unless the branch failed after it was reached.</summary>
    private void ReleaseVerdict(bool branchSucceeded)
    {
        BlockExecutedEventArgs? verdict = _pendingVerdict;
        _pendingVerdict = null;
        if (verdict is not null && branchSucceeded) _deferredBlockExecuted?.Invoke(this, verdict);
    }

    private readonly struct Window
    {
        private readonly ThreadInstructionCounter.Sample _counters;
        private readonly long _allocated;
        private readonly int _gen0;
        private readonly int _gen1;
        private readonly int _gen2;
        private readonly int _threadId;
        private readonly long _jitMethods;
        private readonly long _lockContentions;
        private readonly long _accountHits;
        private readonly long _accountMisses;
        private readonly long _slotHits;
        private readonly long _slotMisses;

        private Window(ThreadInstructionCounter.Sample counters)
        {
            _counters = counters;
            _allocated = GC.GetAllocatedBytesForCurrentThread();
            _gen0 = GC.CollectionCount(0);
            _gen1 = GC.CollectionCount(1);
            _gen2 = GC.CollectionCount(2);
            _threadId = Environment.CurrentManagedThreadId;
            _jitMethods = System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true);
            _lockContentions = Monitor.LockContentionCount;
            _accountHits = FlatMetrics.CarryForwardAccountHits;
            _accountMisses = FlatMetrics.CarryForwardAccountMisses;
            _slotHits = FlatMetrics.CarryForwardSlotHits;
            _slotMisses = FlatMetrics.CarryForwardSlotMisses;
        }

        public static Window Start() => ThreadInstructionCounter.TryRead(out ThreadInstructionCounter.Sample counters) ? new Window(counters) : default;

        public bool IsOnCurrentThread => _threadId != 0 && _threadId == Environment.CurrentManagedThreadId;

        public ulong StartInstructions => _counters.Instructions;

        /// <summary>Formats the window's deltas; false when it never started or ended on another thread.</summary>
        public bool TryStop(out string counts, out ulong instructions)
        {
            counts = string.Empty;
            instructions = 0;
            if (!IsOnCurrentThread) return false;
            if (!ThreadInstructionCounter.TryRead(out ThreadInstructionCounter.Sample end)) return false;

            ThreadInstructionCounter.Sample delta = end - _counters;
            instructions = delta.Instructions;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - _allocated;
            // jit: methods compiled on this thread; lockc: lock contentions anywhere; cfa/cfs: carry-forward cache
            // hits/misses for accounts and slots; bundle/snaps: the flat DB's layering when the window closed.
            counts = $"instr={delta.Instructions} cycles={delta.Cycles} alloc={allocated} " +
                $"gc={GC.CollectionCount(0) - _gen0}/{GC.CollectionCount(1) - _gen1}/{GC.CollectionCount(2) - _gen2} " +
                $"tid={_threadId} mux={(delta.Multiplexed ? 1 : 0)} " +
                $"jit={System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - _jitMethods} " +
                $"lockc={Monitor.LockContentionCount - _lockContentions} " +
                $"cfa={FlatMetrics.CarryForwardAccountHits - _accountHits}/{FlatMetrics.CarryForwardAccountMisses - _accountMisses} " +
                $"cfs={FlatMetrics.CarryForwardSlotHits - _slotHits}/{FlatMetrics.CarryForwardSlotMisses - _slotMisses} " +
                $"bundle={FlatMetrics.SnapshotBundleSize} snaps={FlatMetrics.SnapshotCount}";
            return true;
        }
    }

    /// <summary>The no-GC region a branch runs in, sized down once if the GC mode refuses the first size.</summary>
    private static class NoGcRegion
    {
        // Nethermind's own newPayload region is 512 MB plus 64 MB of large objects.
        private static readonly long[] s_sizes = [576L << 20, 256L << 20, 128L << 20];
        private static int s_sizeIndex;
        private static int s_sizeLogged;
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
                    if (s_entered && Interlocked.Exchange(ref s_sizeLogged, 1) == 0 && logger.IsInfo)
                        logger.Info($"EXPB-COUNT no-GC region per branch: {size >> 20} MB");
                    return;
                }
                catch (ArgumentOutOfRangeException)
                {
                    s_sizeIndex++;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
        }

        /// <summary>Ends the region; false when there was none or a collection ended it inside the window.</summary>
        public static bool Exit()
        {
            if (!s_entered) return false;
            s_entered = false;
            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion) return false;
            try
            {
                GC.EndNoGCRegion();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public event EventHandler<BlockExecutedEventArgs>? BlockExecuted
    {
        add
        {
            if (s_deferVerdict) _deferredBlockExecuted += value;
            else _inner.BlockExecuted += value;
        }
        remove
        {
            if (s_deferVerdict) _deferredBlockExecuted -= value;
            else _inner.BlockExecuted -= value;
        }
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
        _inner.BlockProcessed -= OnBlockProcessed;
        _inner.BranchProcessingCompleted -= OnBranchProcessingCompleted;
        if (_blockProcessor is not null) _blockProcessor.TransactionsExecuted -= OnTransactionsExecuted;
        _inner.BlockExecuted -= OnInnerBlockExecuted;
        _forcedGCExclusion?.Dispose();
        // The container owns the decorated instance; disposing it here would double-dispose.
    }
}
