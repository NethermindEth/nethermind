// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using System.Text;
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

    // Counter readings at FlatDbManager's inline commit steps (see FlatDbManager.CommitPhase), on the committing thread.
    [ThreadStatic] private static ulong[]? t_commitPhases;

    /// <summary><c>NETHERMIND_COUNT_PIN_CPU=n</c> runs each counted branch on CPU n, restoring the thread's CPU set afterwards.</summary>
    private static readonly int s_pinCpu = int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_COUNT_PIN_CPU"), out int cpu) ? cpu : -1;
    private static int s_pinLogged;
    private ulong[]? _savedAffinity;

    /// <summary>
    /// <c>NETHERMIND_COUNT_EXCLUSIVE_CPU=1</c> (with a pin CPU) also moves the process's other threads off that CPU at
    /// every branch start and keeps the processing thread there between branches, so nothing else of the node runs on it.
    /// </summary>
    private static readonly bool s_exclusiveCpu = Environment.GetEnvironmentVariable("NETHERMIND_COUNT_EXCLUSIVE_CPU") == "1";
    private static int s_exclusiveLogged;
    [ThreadStatic] private static int t_commitPhasesSeen;

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

    // NETHERMIND_COUNT_DIAG=1: the diagnostic readings at each window's start, and one buffer for the ends.
    private readonly ulong[]? _branchDiag = DiagnosticCounters.Enabled ? new ulong[3 * DiagnosticCounters.Count] : null;
    private readonly ulong[]? _blockDiag = DiagnosticCounters.Enabled ? new ulong[3 * DiagnosticCounters.Count] : null;
    private readonly ulong[]? _diagScratch = DiagnosticCounters.Enabled ? new ulong[3 * DiagnosticCounters.Count] : null;
    private static int s_hostLogged;

    public CountingBranchProcessor(IBranchProcessor inner, ILogManager logManager, IBlockProcessor? blockProcessor = null)
    {
        _inner = inner;
        Nethermind.State.Flat.FlatDbManager.CommitPhase = OnCommitPhase;
        // NETHERMIND_COUNT_POPSTEPS=1: the cache fill reads the counter between the steps of taking in each node.
        if (Environment.GetEnvironmentVariable("NETHERMIND_COUNT_POPSTEPS") == "1")
            Nethermind.State.Flat.TrieNodeCache.StepCounter = static () => ThreadInstructionCounter.TryRead(out ThreadInstructionCounter.Sample sample) ? sample.Instructions : 0;
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
        if (s_pinCpu >= 0)
        {
            _savedAffinity = ThreadAffinity.PinCurrentThread(s_pinCpu);
            if (Interlocked.Exchange(ref s_pinLogged, 1) == 0 && _logger.IsInfo)
                _logger.Info(_savedAffinity is null ? $"EXPB-COUNT could not pin branches to CPU {s_pinCpu}" : $"EXPB-COUNT branches pinned to CPU {s_pinCpu}");
            if (s_exclusiveCpu)
            {
                // Stays pinned: a restored thread would bring the CPU back to the threads it starts.
                _savedAffinity = null;
                int moved = ThreadAffinity.ExcludeFromOtherThreads(s_pinCpu);
                if (Interlocked.Exchange(ref s_exclusiveLogged, 1) == 0 && _logger.IsInfo)
                    _logger.Info($"EXPB-COUNT CPU {s_pinCpu} kept for block processing: moved {moved} other threads off it");
            }
        }
        // The region's own collection runs here, before the window opens.
        NoGcRegion.TryEnter(_logger);
        _branch = Window.Start(_branchDiag);
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        _executedRead = false;
        _judgedRead = false;
        t_commitPhasesSeen = 0;
        Nethermind.State.Flat.SnapshotCompactor.LastCompaction = (0, 0, -2);
        _block = Window.Start(_blockDiag);
    }

    // Splits the block window: transaction execution before this point, receipts, roots and commit after it.
    private void OnTransactionsExecuted() =>
        _executedRead = _block.IsOnCurrentThread && ThreadInstructionCounter.TryRead(out _executed);

    private void OnBlockProcessed(object? sender, BlockProcessedEventArgs e)
    {
        if (!_block.TryStop(out string counts, out ulong instructions, out ulong cycles, _diagScratch)) return;
        ulong executed = _executedRead ? _executed.Instructions - _block.StartInstructions : 0;
        // roots: receipts, blooms and the state root up to the verdict; commit: the tree commit after it.
        ulong roots = _judgedRead && _executedRead ? _judged.Instructions - _executed.Instructions : 0;
        ulong commit = _judgedRead ? _block.StartInstructions + instructions - _judged.Instructions : 0;
        // cyc: the same three splits in cycles.
        string phaseCycles = _judgedRead && _executedRead
            ? $" cyc={_executed.Cycles - _block.StartCycles}/{_judged.Cycles - _executed.Cycles}/{_block.StartCycles + cycles - _judged.Cycles}"
            : string.Empty;
        // pop: trie node cache population; compact: snapshot compaction; persist: the inline persistence job.
        ulong[]? phases = t_commitPhases;
        const int popParts = (1 << 0) | (1 << 4) | (1 << 5) | (1 << 6) | (1 << 1);
        string popSplit = (t_commitPhasesSeen & popParts) == popParts && phases is not null
            // pre: walk the inputs; loop: add them to the shards; post: memory sum and eviction; release: return the resource.
            ? $" popparts={phases[5] - phases[0]}/{phases[6] - phases[5]}/{phases[4] - phases[6]}/{phases[1] - phases[4]}"
            : string.Empty;
        string steps = (t_commitPhasesSeen & 0b1111) == 0b1111 && phases is not null
            ? $" pop={phases[1] - phases[0]} compact={phases[2] - phases[1]} persist={phases[3] - phases[2]}" +
              $" popslots={Nethermind.State.Flat.TrieNodeCache.LastAddSlots} popnodes={Nethermind.State.Flat.TrieNodeCache.LastAddNodes}" +
              $" popclear={Nethermind.State.Flat.TrieNodeCache.LastAddShardsCleared}" + PopSteps()
            : string.Empty;
        // cmp: snapshots the inline compaction merged / how many were already compacted / added (1), refused (0),
        // nothing assembled (-1), not attempted (-2).
        (int inputs, int compactedInputs, int added) = Nethermind.State.Flat.SnapshotCompactor.LastCompaction;
        Block block = e.Block;
        // host: busy jiffies since the previous block on the pinned CPU / the process's other CPUs / every other CPU;
        // mem: resident / huge-page-backed anonymous kB, every 100th block.
        string host = DiagnosticCounters.Enabled
            ? $" host={HostActivity.Delta(s_pinCpu)}" + (block.Number % 100 == 0 ? $" mem={HostActivity.Memory()}" : string.Empty)
            : string.Empty;
        if (_logger.IsInfo) _logger.Info($"EXPB-COUNT block={block.Number} txs={block.Transactions.Length} gas={block.GasUsed} {counts} exec={executed} post={instructions - executed} roots={roots} commit={commit}{steps}{popSplit} cmp={inputs}/{compactedInputs}/{added}{phaseCycles}{host}");
        if (DiagnosticCounters.Enabled && Interlocked.Exchange(ref s_hostLogged, 1) == 0 && _logger.IsInfo)
            _logger.Info($"EXPB-COUNT host{HostActivity.Facts(s_pinCpu)}");
    }

    // popsteps: the cache fill's per-node steps summed over the block (see TrieNodeCache.LastAddSteps).
    private static string PopSteps()
    {
        if (Nethermind.State.Flat.TrieNodeCache.StepCounter is null) return string.Empty;
        (ulong pruneNew, ulong sizeNew, ulong exchange, ulong old, long added, long replaced) = Nethermind.State.Flat.TrieNodeCache.LastAddSteps;
        return $" popsteps={pruneNew}/{sizeNew}/{exchange}/{old}/{added}/{replaced}";
    }

    private static void OnCommitPhase(int phase)
    {
        if ((uint)phase >= 7 || !ThreadInstructionCounter.TryRead(out ThreadInstructionCounter.Sample sample)) return;
        (t_commitPhases ??= new ulong[7])[phase] = sample.Instructions;
        t_commitPhasesSeen |= 1 << phase;
    }

    private void OnInnerBlockExecuted(object? sender, BlockExecutedEventArgs e)
    {
        _judgedRead = _block.IsOnCurrentThread && ThreadInstructionCounter.TryRead(out _judged);
        if (s_deferVerdict) _pendingVerdict = e;
    }

    private void OnBranchProcessingCompleted(object? sender, BranchProcessingCompletedEventArgs e)
    {
        bool stopped = _branch.TryStop(out string counts, out _, _diagScratch);
        bool regionHeld = NoGcRegion.Exit();
        if (_savedAffinity is not null)
        {
            ThreadAffinity.Restore(_savedAffinity);
            _savedAffinity = null;
        }
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
        private readonly long _leaseSpins;
        // NETHERMIND_COUNT_DIAG=1: the diagnostic events and the wall clock at the start, read before the counters.
        private readonly ulong[]? _diag;
        private readonly long _startTimestamp;

        private Window(ThreadInstructionCounter.Sample counters, ulong[]? diag, long startTimestamp)
        {
            _diag = diag;
            _startTimestamp = startTimestamp;
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
            _leaseSpins = FlatMetrics.TransientLeaseSpins;
        }

        public static Window Start(ulong[]? diag = null)
        {
            // The diagnostic reads come first here and last in TryStop, so their syscalls stay outside the window.
            bool diagRead = diag is not null && DiagnosticCounters.TryRead(diag);
            long startTimestamp = Stopwatch.GetTimestamp();
            return ThreadInstructionCounter.TryRead(out ThreadInstructionCounter.Sample counters)
                ? new Window(counters, diagRead ? diag : null, startTimestamp)
                : default;
        }

        public bool IsOnCurrentThread => _threadId != 0 && _threadId == Environment.CurrentManagedThreadId;

        public ulong StartInstructions => _counters.Instructions;

        public ulong StartCycles => _counters.Cycles;

        /// <summary>Formats the window's deltas; false when it never started or ended on another thread.</summary>
        public bool TryStop(out string counts, out ulong instructions, ulong[]? diagScratch = null) => TryStop(out counts, out instructions, out _, diagScratch);

        public bool TryStop(out string counts, out ulong instructions, out ulong cycles, ulong[]? diagScratch = null)
        {
            counts = string.Empty;
            instructions = 0;
            cycles = 0;
            if (!IsOnCurrentThread) return false;
            if (!ThreadInstructionCounter.TryRead(out ThreadInstructionCounter.Sample end)) return false;
            long endTimestamp = Stopwatch.GetTimestamp();
            bool diagRead = _diag is not null && diagScratch is not null && DiagnosticCounters.TryRead(diagScratch);

            ThreadInstructionCounter.Sample delta = end - _counters;
            instructions = delta.Instructions;
            cycles = delta.Cycles;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - _allocated;
            string diag = string.Empty;
            if (diagRead)
            {
                StringBuilder text = new();
                text.Append(" us=").Append((long)Stopwatch.GetElapsedTime(_startTimestamp, endTimestamp).TotalMicroseconds);
                DiagnosticCounters.AppendDeltas(text, _diag, diagScratch);
                diag = text.ToString();
            }
            // jit: methods compiled on this thread; lockc: lock contentions anywhere; cfa/cfs: carry-forward cache
            // hits/misses for accounts and slots; bundle/snaps: the flat DB's layering when the window closed.
            counts = $"instr={delta.Instructions} cycles={delta.Cycles} alloc={allocated} " +
                $"gc={GC.CollectionCount(0) - _gen0}/{GC.CollectionCount(1) - _gen1}/{GC.CollectionCount(2) - _gen2} " +
                $"tid={_threadId} mux={(delta.Multiplexed ? 1 : 0)} gcs={_gen0}/{_gen1}/{_gen2} " +
                $"jit={System.Runtime.JitInfo.GetCompiledMethodCount(currentThread: true) - _jitMethods} " +
                $"lockc={Monitor.LockContentionCount - _lockContentions} " +
                $"cfa={FlatMetrics.CarryForwardAccountHits - _accountHits}/{FlatMetrics.CarryForwardAccountMisses - _accountMisses} " +
                $"cfs={FlatMetrics.CarryForwardSlotHits - _slotHits}/{FlatMetrics.CarryForwardSlotMisses - _slotMisses} " +
                $"bundle={FlatMetrics.SnapshotBundleSize} snaps={FlatMetrics.SnapshotCount} spins={FlatMetrics.TransientLeaseSpins - _leaseSpins}" +
                diag;
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
