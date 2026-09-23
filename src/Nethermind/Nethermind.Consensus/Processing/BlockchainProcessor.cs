// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Metrics = Nethermind.Blockchain.Metrics;
using static Nethermind.Core.Threading.ProcessingThread;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// The main block processing pipeline: queues suggested blocks, recovers their data and processes them on the
/// main world state.
/// </summary>
/// <remarks>
/// This is for main block processing only and is registered solely in the main processing context. It carries
/// a lot of main-specific logic (processing queue and thread, head updates, invalid block handling, stats,
/// diagnostic dumps), so other environments should use another implementation such as
/// <see cref="OneTimeChainProcessor"/> or <see cref="MainStateBlockBuildingChainProcessor"/>, or call
/// <see cref="IBranchProcessor"/> directly.
/// </remarks>
public sealed class BlockchainProcessor : IBlockchainProcessor, IBlockProcessingQueue, IBlockProcessingPauseControl
{
    public int SoftMaxRecoveryQueueSizeInTx = 10000; // adjust based on tx or gas
    public const int MaxProcessingQueueSize = 2048; // adjust based on tx or gas

    public static bool IsMainProcessingThread => IsBlockProcessingThread;

    private readonly IBranchProcessor _branchProcessor;
    private readonly ISpecProvider _specProvider;
    private readonly Options _options;
    private readonly ProcessingBranchBuilder _branchBuilder;
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;

    private readonly Channel<BlockRef> _recoveryQueue = Channel.CreateUnbounded<BlockRef>(
        new UnboundedChannelOptions()
        {
            // Optimize for single reader concurrency
            SingleReader = true,
        });

    private readonly Channel<BlockRef> _blockQueue = Channel.CreateBounded<BlockRef>(
        new BoundedChannelOptions(MaxProcessingQueueSize)
        {
            // Optimize for single reader concurrency
            SingleReader = true,
            // If queues are empty we want the block processing to continue on NewPayload thread and inherit its priority
            AllowSynchronousContinuations = true,
        });

    private bool _recoveryComplete = false;
    private int _queueCount;
    private bool _disposed;
    // Every block between Enqueue and its BlockRemoved, counted per copy: the engine API and sync can queue the
    // same hash twice, and a waiter is released only once the last copy is gone.
    private readonly ConcurrentDictionary<Hash256, InFlightBlock> _inFlight = new();

    private readonly IProcessingStats _stats;

    private CancellationTokenSource? _loopCancellationSource;
    private Task? _recoveryTask;
    private Task? _processorTask;
    private DateTime _lastProcessedBlock;

    private int _currentRecoveryQueueSize;
    private bool _isProcessingBlock;
    private const int MaxBranchSize = 8192;
    private readonly CompositeBlockTracer _compositeBlockTracer = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly BlockProcessingPauseGate _pauseGate = new();

    /// <summary>
    ///
    /// </summary>
    /// <param name="blockTree"></param>
    /// <param name="branchProcessor"></param>
    /// <param name="specProvider">Provider used to select fork rules while tracing invalid branches.</param>
    /// <param name="preprocessorSteps"></param>
    /// <param name="stateReader"></param>
    /// <param name="logManager"></param>
    /// <param name="options"></param>
    /// <param name="processingStats"></param>
    /// <param name="blockTracers">Tracers seeded into the processor's composite tracer at construction.</param>
    public BlockchainProcessor(
        IBlockTree blockTree,
        IBranchProcessor branchProcessor,
        ISpecProvider specProvider,
        IReadOnlyList<IBlockPreprocessorStep> preprocessorSteps,
        IStateReader stateReader,
        ILogManager logManager,
        Options options,
        IProcessingStats processingStats,
        IEnumerable<IBlockTracer>? blockTracers = null)
    {
        _logger = logManager.GetClassLogger<BlockchainProcessor>();
        _blockTree = blockTree;
        _branchProcessor = branchProcessor;
        _specProvider = specProvider;
        _options = options;
        _branchBuilder = new ProcessingBranchBuilder(blockTree, stateReader, preprocessorSteps, logManager.GetClassLogger<ProcessingBranchBuilder>());

        _stats = processingStats;
        _loopCancellationSource = new CancellationTokenSource();
        _stats.NewProcessingStatistics += OnNewProcessingStatistics;
        _branchProcessor.BlockExecuted += OnBlockExecuted;
        if (blockTracers is not null) _compositeBlockTracer.AddRange(blockTracers);
    }

    private void OnBlockExecuted(object? sender, BlockExecutedEventArgs e)
    {
        Block block = e.Block;
        Hash256 hash = block.Hash!;
        if (_inFlight.TryGetValue(hash, out InFlightBlock? inFlight)) inFlight.MarkExecuted();

        try
        {
            BlockExecuted?.Invoke(this, new BlockHashEventArgs(hash, block.IsInclusionListSatisfied ? ProcessingResult.Success : ProcessingResult.InclusionListUnsatisfied));
        }
        catch (Exception exception)
        {
            // The block is judged and the commit is next; a subscriber must not be able to turn that into a failure
            // after the verdict has gone out, which is what an exception here would unwind into.
            if (_logger.IsError) _logger.Error($"Block executed handler failed for {hash}.", exception);
        }
    }

    /// <inheritdoc/>
    public ValueTask WaitUntilRemovedAsync(Hash256 blockHash, bool executedOnly = false)
        => _inFlight.TryGetValue(blockHash, out InFlightBlock? inFlight) && (!executedOnly || inFlight.Executed)
            ? new ValueTask(inFlight.Removed)
            : ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask WaitUntilExecutedCopyRemovedAsync(Hash256 blockHash)
        => _inFlight.TryGetValue(blockHash, out InFlightBlock? inFlight)
            ? new ValueTask(inFlight.ExecutedCopyRemoved)
            : ValueTask.CompletedTask;

    private void TrackInFlight(Hash256 blockHash)
    {
        // An entry whose last copy has just left refuses the copy while its removal is still under way, a matter of
        // a few instructions on another thread; the next lookup creates a fresh one.
        SpinWait spinner = default;
        while (!_inFlight.GetOrAdd(blockHash, static _ => new InFlightBlock()).TryAddCopy()) spinner.SpinOnce();
    }

    /// <remarks>
    /// The removal is published before the waiters are released: a waiter that resumes must never find an event of
    /// the copy it waited out still pending, or it could take that event for its own. The release does not depend on
    /// the handlers, so a throwing one cannot leave a waiter parked.
    /// </remarks>
    private void OnBlockRemoved(BlockRemovedEventArgs e)
    {
        try
        {
            BlockRemoved?.Invoke(this, e);
        }
        catch (Exception exception)
        {
            // Not rethrown: the processing loop would report this removal a second time through its own catch, and
            // a second report takes a copy off whatever entry the hash names by then - after a re-enqueue, a live one.
            if (_logger.IsError) _logger.Error($"Block removed handler failed for {e.BlockHash}.", exception);
        }
        finally
        {
            if (_inFlight.TryGetValue(e.BlockHash, out InFlightBlock? inFlight) && inFlight.RemoveCopy())
            {
                _inFlight.TryRemove(new KeyValuePair<Hash256, InFlightBlock>(e.BlockHash, inFlight));
            }
        }
    }

    /// <summary>
    /// The copies of one block hash between enqueue and removal, and the waiters for the last of them to go. A
    /// waiter's source is created only when someone waits; once the last copy is removed the slot holds a
    /// completed sentinel, so a waiter arriving later finds it done.
    /// </summary>
    private sealed class InFlightBlock
    {
        private static readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _copies;
        private TaskCompletionSource? _removed;
        private TaskCompletionSource? _executedCopyRemoved;
        private volatile bool _executed;

        static InFlightBlock() => Done.SetResult();

        /// <summary>Whether a copy has had its verdict: from here to removal the block is committing.</summary>
        public bool Executed => _executed;

        public void MarkExecuted() => _executed = true;

        public Task Removed
        {
            get
            {
                TaskCompletionSource? removed = Volatile.Read(ref _removed);
                if (removed is null)
                {
                    TaskCompletionSource created = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    removed = Interlocked.CompareExchange(ref _removed, created, null) ?? created;
                }

                return removed.Task;
            }
        }

        /// <summary>Completes when the copy that has had its verdict is removed; done at once when no copy has had one.</summary>
        public Task ExecutedCopyRemoved
        {
            get
            {
                TaskCompletionSource? removed = Volatile.Read(ref _executedCopyRemoved);
                if (removed is null)
                {
                    TaskCompletionSource created = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    removed = Interlocked.CompareExchange(ref _executedCopyRemoved, created, null) ?? created;
                }

                // Read after the source is published: a removal that cleared the flag before then took no source to
                // release, so this one would never complete.
                return _executed ? removed.Task : Task.CompletedTask;
            }
        }

        /// <summary><c>false</c> once the last copy has been removed; the entry is then being taken out.</summary>
        public bool TryAddCopy()
        {
            int copies = Volatile.Read(ref _copies);
            while (copies >= 0)
            {
                int seen = Interlocked.CompareExchange(ref _copies, copies + 1, copies);
                if (seen == copies) return true;
                copies = seen;
            }

            return false;
        }

        /// <summary><c>true</c> when this was the last copy, so the entry is to be removed and its waiters are released.</summary>
        /// <remarks>
        /// Removals are raised once per copy and in sequence today; the clamps keep a second removal of the last copy,
        /// should one ever overlap, from stranding the entry at a count nothing can bring back to zero - which would
        /// leave every waiter on the hash to its full bound and spin the next enqueue of it forever.
        /// </remarks>
        public bool RemoveCopy()
        {
            // Copies are processed in order, so none of the ones left has had its verdict. The next one sets the flag
            // again when its own lands; until then an executed-only wait must not take the entry for a block that is
            // committing. The flag is cleared before the source is taken, which is the order ExecutedCopyRemoved
            // relies on.
            if (_executed)
            {
                _executed = false;
                Interlocked.Exchange(ref _executedCopyRemoved, null)?.TrySetResult();
            }

            int copies = Interlocked.Decrement(ref _copies);
            if (copies > 0) return false;
            // Taken below zero by a removal that overlapped the last one: that one owns the release either way.
            if (copies < 0) return false;
            // A copy queued between the decrement and here keeps the entry alive, and the waiters wait for it too.
            if (Interlocked.CompareExchange(ref _copies, -1, 0) != 0) return false;
            Release();
            return true;
        }

        public void Release()
        {
            Interlocked.Exchange(ref _executedCopyRemoved, Done)?.TrySetResult();
            Interlocked.Exchange(ref _removed, Done)?.TrySetResult();
        }
    }

    private void Preprocess(Block block) => _branchBuilder.PreprocessQueued(block);

    private void OnNewProcessingStatistics(object? sender, BlockStatistics stats)
        => NewProcessingStatistics?.Invoke(sender, stats);

    private void OnNewHeadBlock(object? sender, BlockEventArgs e) => _lastProcessedBlock = DateTime.UtcNow;

    private void OnNewBestBlock(object sender, BlockEventArgs blockEventArgs)
    {
        ProcessingOptions options = ProcessingOptions.None;
        if (_options.StoreReceiptsByDefault)
        {
            options |= ProcessingOptions.StoreReceipts;
        }

        if (blockEventArgs.Block is not null)
        {
            _ = Enqueue(blockEventArgs.Block, options);
        }
    }

    public async ValueTask Enqueue(Block block, ProcessingOptions processingOptions)
    {
        if (_logger.IsTrace) _logger.Trace($"Enqueuing a new block {block.ToString(Block.Format.Short)} for processing.");

        Hash256? blockHash = block.Hash!;
        // InclusionListTransactions aren't in RLP, so a hash-only ref re-resolved from the DB would drop
        // them and pass a censoring payload.
        BlockRef blockRef = _currentRecoveryQueueSize >= SoftMaxRecoveryQueueSizeInTx && block.InclusionListTransactions is null
            ? new BlockRef(blockHash, processingOptions)
            : new BlockRef(block, processingOptions);

        if (!_recoveryComplete)
        {
            Interlocked.Increment(ref _queueCount);
            TrackInFlight(blockHash);
            BlockAdded?.Invoke(this, new BlockEventArgs(block));

            _lastProcessedBlock = DateTime.UtcNow;
            try
            {
                if (blockRef.Resolve(_blockTree))
                {
                    if (_logger.IsTrace) _logger.Trace($"A new block {block.ToString(Block.Format.Short)} enqueued for processing.");
                    if (_queueCount > 1)
                    {
                        Interlocked.Add(ref _currentRecoveryQueueSize, block.Transactions.Length);
                        if (!_recoveryQueue.Writer.TryWrite(blockRef))
                        {
                            // Refused only once the queue is completed, at shutdown. Dropped silently it would leave
                            // the in-flight entry a copy nothing takes off, and every later wait on that hash hanging.
                            Interlocked.Add(ref _currentRecoveryQueueSize, -block.Transactions.Length);
                            DecrementQueue(blockRef.BlockHash, ProcessingResult.QueueException);
                            return;
                        }
                    }
                    else
                    {
                        // Skip recovery queue if nothing in queue
                        if (!_blockQueue.Writer.TryWrite(blockRef))
                        {
                            await _blockQueue.Writer.WriteAsync(blockRef);
                        }
                    }
                }
                else
                {
                    DecrementQueue(blockRef.BlockHash, ProcessingResult.MissingBlock);
                    if (_logger.IsTrace) _logger.Trace("Block was removed from the DB and cannot be recovered (it belonged to an invalid branch). Skipping.");
                }
            }
            catch (Exception e)
            {
                Interlocked.Decrement(ref _queueCount);
                OnBlockRemoved(new BlockRemovedEventArgs(blockHash, ProcessingResult.QueueException, e));
                if (e is not InvalidOperationException || !_recoveryComplete)
                {
                    throw;
                }
            }
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_processorTask is not null) ThrowAlreadyStarted();

        _blockTree.NewBestSuggestedBlock += OnNewBestBlock;
        _blockTree.NewHeadBlock += OnNewHeadBlock;

        _loopCancellationSource ??= new CancellationTokenSource();
        _recoveryTask = RunRecovery();
        _processorTask = RunProcessing();

        if (_logger.IsInfo) _logger.Info($"{nameof(BlockchainProcessor)} started.");

        [StackTraceHidden, DoesNotReturn]
        static void ThrowAlreadyStarted() => throw new InvalidOperationException($"{nameof(BlockchainProcessor)} already started");
    }

    public bool IsPaused => _pauseGate.IsPaused;

    public void Pause()
    {
        if (_pauseGate.Pause() && _logger.IsInfo) _logger.Info("Block processing paused.");
    }

    public void Resume()
    {
        if (_pauseGate.Resume() && _logger.IsInfo) _logger.Info("Block processing resumed.");
    }

    public async Task StopAsync(bool processRemainingBlocks = false)
    {
        if (_disposed) return;
        _disposed = true;

        bool isStarted = _processorTask is not null;
        if (isStarted)
        {
            _blockTree.NewBestSuggestedBlock -= OnNewBestBlock;
            _blockTree.NewHeadBlock -= OnNewHeadBlock;
        }

        _recoveryComplete = true;
        if (processRemainingBlocks)
        {
            _pauseGate.Resume();
            _recoveryQueue.Writer.TryComplete();
            await (_recoveryTask ?? Task.CompletedTask);
            _blockQueue.Writer.TryComplete();
        }
        else
        {
            CancellationTokenExtensions.CancelDisposeAndClear(ref _loopCancellationSource);
            _recoveryQueue.Writer.TryComplete();
            _blockQueue.Writer.TryComplete();
        }

        try
        {
            await Task.WhenAll(_recoveryTask ?? Task.CompletedTask, _processorTask ?? Task.CompletedTask);
        }
        finally
        {
            _branchProcessor.BlockExecuted -= OnBlockExecuted;
            // Blocks still queued when the loops ended get no BlockRemoved; whoever waits for them is let go here,
            // whether or not a loop faulted, since a waiter may be holding the engine API's lock.
            foreach (KeyValuePair<Hash256, InFlightBlock> inFlight in _inFlight)
            {
                inFlight.Value.Release();
            }

            _inFlight.Clear();
        }

        if (isStarted && _logger.IsInfo) _logger.Info($"{nameof(BlockchainProcessor)} shutdown complete.");
    }

    private async Task RunRecovery()
    {
        try
        {
            await RunRecoveryLoop();
            if (_logger.IsDebug) _logger.Debug("Sender address recovery complete.");
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsDebug) _logger.Debug("Sender address recovery stopped.");
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("Sender address recovery encountered an exception.", ex);
        }
    }

    private void DecrementQueue(Hash256 blockHash, ProcessingResult processingResult, Exception? exception = null)
    {
        Interlocked.Decrement(ref _queueCount);
        OnBlockRemoved(new BlockRemovedEventArgs(blockHash, processingResult, exception));
        FireProcessingQueueEmpty();
    }

    private async Task RunRecoveryLoop()
    {
        if (_logger.IsDebug) _logger.Debug($"Starting recovery loop - {_blockQueue.Reader.Count} blocks waiting in the queue.");
        _lastProcessedBlock = DateTime.UtcNow;
        await foreach (BlockRef blockRef in _recoveryQueue.Reader.ReadAllAsync(CancellationToken))
        {
            bool notified = false;
            try
            {
                Interlocked.Add(ref _currentRecoveryQueueSize, -blockRef.Block!.Transactions.Length);
                if (_logger.IsTrace) _logger.Trace($"Recovering addresses for block {blockRef.BlockHash}.");
                Preprocess(blockRef.Block);

                try
                {
                    await _blockQueue.Writer.WriteAsync(blockRef);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    DecrementQueue(blockRef.BlockHash, ProcessingResult.QueueException, e);
                    notified = true;

                    if (e is InvalidOperationException)
                    {
                        if (_logger.IsDebug) _logger.Debug($"Recovery loop stopping.");
                        return;
                    }

                    throw;
                }
            }
            catch (Exception e)
            {
                // Once per queued copy. A second removal for the same block takes a copy off whatever entry the
                // hash names by then, which after a re-enqueue is a live one, and releases its waiters early.
                if (!notified) DecrementQueue(blockRef.BlockHash, ProcessingResult.Exception, e);
                throw;
            }
        }
    }

    private CancellationToken CancellationToken
        => _loopCancellationSource?.Token ?? CancellationTokenExtensions.AlreadyCancelledToken;

    private async Task RunProcessing()
    {
        try
        {
            await RunProcessingLoop();
            if (_logger.IsDebug) _logger.Debug($"{nameof(BlockchainProcessor)} complete.");
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsDebug) _logger.Debug($"{nameof(BlockchainProcessor)} stopped.");
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"{nameof(BlockchainProcessor)} encountered an exception.", ex);
        }
    }

    private bool IsProcessingBlock { get => _isProcessingBlock; set { _isProcessingBlock = value; _blockTree.IsProcessingBlock = value; } }

    private async Task RunProcessingLoop()
    {
        if (_logger.IsDebug) _logger.Debug($"Starting block processor - {_blockQueue.Reader.Count} blocks waiting in the queue.");

        FireProcessingQueueEmpty();

        GCScheduler.Instance.SwitchOnBackgroundGC(0);
        while (await _blockQueue.Reader.WaitToReadAsync(CancellationToken))
        {
            await _pauseGate.WaitWhilePausedAsync(CancellationToken);

            using ThreadExtensions.Disposable handle = Thread.CurrentThread.SetHighestPriority();
            // Have block, switch off background GC timer
            GCScheduler.Instance.SwitchOffBackgroundGC(_blockQueue.Reader.Count);
            IsProcessingBlock = true;
            bool previousMainThread = IsBlockProcessingThread;
            IsBlockProcessingThread = true;
            try
            {
                ProcessBlocks();
            }
            finally
            {
                IsBlockProcessingThread = previousMainThread;
                IsProcessingBlock = false;
            }

            if (_logger.IsTrace) Trace();
            FireProcessingQueueEmpty();

            GCScheduler.Instance.SwitchOnBackgroundGC(_blockQueue.Reader.Count);
        }

        if (_logger.IsInfo) _logger.Info("Block processor queue stopped.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace() => _logger.Trace($"Now {_blockQueue.Reader.Count} blocks waiting in the queue.");
    }

    private void ProcessBlocks()
    {
        bool isTrace = _logger.IsTrace;
        while (!_pauseGate.IsPaused && _blockQueue.Reader.TryRead(out BlockRef blockRef))
        {
            try
            {
                if (blockRef.IsInDb || blockRef.Block is null)
                {
                    ThrowIncorrectBlockReference(blockRef);
                }

                Block block = blockRef.Block;
                if (isTrace) TraceProcessing(block);

                _stats.Start();
                Block processedBlock = Process(block, blockRef.ProcessingOptions, _compositeBlockTracer.GetTracer(), CancellationToken, out string? error);

                if (processedBlock is null)
                {
                    NotifyFailedOrSkipped(blockRef, block, error);
                }
                else if (!processedBlock.IsInclusionListSatisfied)
                {
                    // The block was committed normally; signal the CL via newPayload status only.
                    NotifyInclusionListUnsatisfied(blockRef, processedBlock);
                }
                else
                {
                    if (isTrace) TraceProcessed(block);
                    OnBlockRemoved(new BlockRemovedEventArgs(blockRef.BlockHash, ProcessingResult.Success));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                NotifyException(blockRef, exception);
            }
            finally
            {
                Interlocked.Decrement(ref _queueCount);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void NotifyException(BlockRef blockRef, Exception exception)
        {
            if (_logger.IsWarn) _logger.Warn($"Processing block failed. Block: {blockRef}, Exception: {exception}");
            OnBlockRemoved(new BlockRemovedEventArgs(blockRef.BlockHash, ProcessingResult.Exception, exception));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void NotifyFailedOrSkipped(BlockRef blockRef, Block block, string error)
        {
            if (_logger.IsTrace) _logger.Trace($"Failed / skipped processing {block.ToString(Block.Format.Full)}");
            OnBlockRemoved(new BlockRemovedEventArgs(blockRef.BlockHash, ProcessingResult.ProcessingError, error));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void NotifyInclusionListUnsatisfied(BlockRef blockRef, Block block)
        {
            if (_logger.IsTrace) _logger.Trace($"Inclusion list unsatisfied for block {block.ToString(Block.Format.Full)}");
            OnBlockRemoved(new BlockRemovedEventArgs(blockRef.BlockHash, ProcessingResult.InclusionListUnsatisfied));
        }

        // Reported by the catch below, which every other failure here goes through too: reporting twice would take a
        // second copy off whatever entry the hash names by then, and after a re-enqueue that is a live one.
        [DoesNotReturn]
        static void ThrowIncorrectBlockReference(BlockRef blockRef) =>
            throw new InvalidOperationException("Block processing expects only resolved blocks");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceProcessing(Block block) => _logger.Trace($"Processing block {block.ToString(Block.Format.Short)}).");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceProcessed(Block block) => _logger.Trace($"Processed block {block.ToString(Block.Format.Full)}");
    }

    private void FireProcessingQueueEmpty()
    {
        if (IsEmpty)
        {
            ProcessingQueueEmpty?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ProcessingQueueEmpty;
    public event EventHandler<BlockHashEventArgs>? BlockExecuted;
    public event EventHandler<BlockRemovedEventArgs>? BlockRemoved;
    public event EventHandler<BlockEventArgs>? BlockAdded;
    public event EventHandler<IBlockProcessingQueue.InvalidBlockEventArgs>? InvalidBlock;
    public event EventHandler<BlockStatistics>? NewProcessingStatistics;
    public bool IsEmpty => Volatile.Read(ref _queueCount) == 0;
    public int Count => Volatile.Read(ref _queueCount);

    public Block? Process(Block suggestedBlock, ProcessingOptions options, IBlockTracer tracer, CancellationToken token = default) =>
        Process(suggestedBlock, options, tracer, token, out _);

    public Block? Process(Block suggestedBlock, ProcessingOptions options, IBlockTracer tracer, CancellationToken token, out string? error)
    {
        error = null;
        if (!_branchBuilder.RunSimpleChecksAheadOfProcessing(suggestedBlock, options))
        {
            return null;
        }

        UInt256 totalDifficulty = suggestedBlock.TotalDifficulty ?? 0;
        if (_logger.IsTrace) _logger.Trace($"Total difficulty of block {suggestedBlock.ToString(Block.Format.Short)} is {totalDifficulty}");

        bool shouldProcess =
            suggestedBlock.IsGenesis
            || _blockTree.IsBetterThanHead(suggestedBlock.Header)
            || options.ContainsFlag(ProcessingOptions.ForceProcessing);

        if (!shouldProcess)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}, Head = {_blockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}, total diff = {totalDifficulty}, head total diff = {_blockTree.Head?.TotalDifficulty}");
            return null;
        }

        _stats.CaptureStartStats();

        using ProcessingBranch processingBranch = PrepareProcessingBranch(suggestedBlock, options);
        _branchBuilder.PrepareBlocksToProcess(suggestedBlock, options, processingBranch, token);

        _stopwatch.Restart();
        Block[]? processedBlocks = ProcessBranch(processingBranch, options, tracer, token, out error);
        _stopwatch.Stop();
        if (processedBlocks is null)
        {
            return null;
        }

        Block? lastProcessed = null;
        if (processedBlocks.Length > 0)
        {
            lastProcessed = processedBlocks[^1];
            if (_logger.IsTrace) _logger.Trace($"Setting total on last processed to {lastProcessed.ToString(Block.Format.Short)}");
            lastProcessed.Header.TotalDifficulty = suggestedBlock.TotalDifficulty;
        }
        else
        {
            if (_logger.IsDebug) _logger.Debug($"Skipped processing of {suggestedBlock.ToString(Block.Format.FullHashAndNumber)}, last processed is null: {true}, processedBlocks.Length: {processedBlocks.Length}");
        }

        long blockProcessingTimeInMicrosecs = _stopwatch.ElapsedMicroseconds();
        Metrics.LastBlockProcessingTimeInMs = blockProcessingTimeInMicrosecs / 1000;
        int blockQueueCount = _blockQueue.Reader.Count;
        Metrics.RecoveryQueueSize = Math.Max(_queueCount - blockQueueCount - (IsProcessingBlock ? 1 : 0), 0);
        Metrics.ProcessingQueueSize = blockQueueCount;
        _stats.UpdateStats(processedBlocks, processingBranch.BaseBlock, blockProcessingTimeInMicrosecs);

        bool updateHead = !options.ContainsFlag(ProcessingOptions.DoNotUpdateHead);
        if (updateHead)
        {
            if (_logger.IsTrace) _logger.Trace($"Updating main chain: {lastProcessed}, blocks count: {processedBlocks.Length}");
            // Pass the just-processed blocks as a cache; TryUpdateMainChain walks the rest of the branch
            // (any deeper blocks that already had state) on its own, loading them one at a time.
            if (!_blockTree.TryUpdateMainChain(suggestedBlock.Header, wereProcessed: true, preloadedBlocks: processingBranch.Blocks.AsSpan()) && _logger.IsWarn)
                _logger.Warn($"Failed to update main chain to {suggestedBlock.ToString(Block.Format.Short)}; a branch predecessor is missing.");
        }

        if ((options & ProcessingOptions.MarkAsProcessed) == ProcessingOptions.MarkAsProcessed)
        {
            if (_logger.IsTrace) _logger.Trace($"Marked blocks as processed {lastProcessed}, blocks count: {processedBlocks.Length}");
            _blockTree.MarkChainAsProcessed(processingBranch.Blocks);
        }

        Metrics.BestKnownBlockNumber = _blockTree.BestKnownNumber;

        return lastProcessed;
    }

    public bool IsProcessingBlocks(ulong? maxProcessingInterval) =>
        _processorTask?.IsCompleted == false && _recoveryTask?.IsCompleted == false &&
        (_pauseGate.IsPaused || maxProcessingInterval is null || _lastProcessedBlock.AddSeconds(maxProcessingInterval.Value) > DateTime.UtcNow);

    private void TraceFailingBranch(in ProcessingBranch processingBranch, ProcessingOptions options, IBlockTracer blockTracer, DumpOptions dumpType)
    {
        if ((_options.DumpOptions & dumpType) != 0)
        {
            try
            {
                _branchProcessor.Process(
                    processingBranch.BaseBlock,
                    processingBranch.BlocksToProcess,
                    options,
                    blockTracer);
                BlockTraceDumper.LogDiagnosticTrace(blockTracer, processingBranch.BlocksToProcess, _logger);
            }
            catch (InvalidBlockException ex)
            {
                BlockTraceDumper.LogDiagnosticTrace(blockTracer, ex.InvalidBlock.Hash!, _logger);
            }
            catch (Exception ex)
            {
                BlockTraceDumper.LogTraceFailure(blockTracer, processingBranch.BaseBlock, ex, _logger);
            }
        }
    }

    private Block[]? ProcessBranch(in ProcessingBranch processingBranch, ProcessingOptions options, IBlockTracer tracer, CancellationToken token, out string? error)
    {
        void DeleteInvalidBlocks(in ProcessingBranch processingBranch, Hash256 invalidBlockHash)
        {
            for (int i = 0; i < processingBranch.BlocksToProcess.Count; i++)
            {
                if (processingBranch.BlocksToProcess[i].Hash == invalidBlockHash)
                {
                    _blockTree.DeleteInvalidBlock(processingBranch.BlocksToProcess[i]);
                    if (_logger.IsDebug) _logger.Debug($"Skipped processing of {processingBranch.BlocksToProcess[^1].ToString(Block.Format.FullHashAndNumber)} because of {processingBranch.BlocksToProcess[i].ToString(Block.Format.FullHashAndNumber)} is invalid");
                }
            }
        }

        Hash256? invalidBlockHash = null;
        Block[]? processedBlocks;
        try
        {
            processedBlocks = _branchProcessor.Process(
                processingBranch.BaseBlock,
                processingBranch.BlocksToProcess,
                options,
                tracer,
                token);
            error = null;
        }
        catch (InvalidBlockException ex)
        {
            if (_logger.IsWarn) _logger.Warn($"Issue processing block {ex.InvalidBlock} {ex}");
            invalidBlockHash = ex.InvalidBlock.Hash;
            error = ex.Message;
            Block? invalidBlock = processingBranch.BlocksToProcess.FirstOrDefault(b => b.Hash == invalidBlockHash);
            if (invalidBlock is not null)
            {
                Metrics.BadBlocks++;
                if (ex.InvalidBlock.IsByNethermindNode())
                {
                    Metrics.BadBlocksByNethermindNodes++;
                }
                InvalidBlock?.Invoke(this, new IBlockProcessingQueue.InvalidBlockEventArgs { InvalidBlock = invalidBlock, });

                BlockTraceDumper.LogDiagnosticRlp(invalidBlock, _logger,
                    (_options.DumpOptions & DumpOptions.Rlp) != 0,
                    (_options.DumpOptions & DumpOptions.RlpLog) != 0);

                TraceFailingBranch(
                    processingBranch,
                    options,
                    new BlockReceiptsTracer(),
                    DumpOptions.Receipts);

                TraceFailingBranch(
                    processingBranch,
                    options,
                    new ParityLikeBlockTracer(ParityTraceTypes.StateDiff | ParityTraceTypes.Trace),
                    DumpOptions.Parity);

                TraceFailingBranch(
                    processingBranch,
                    options,
                    new GethLikeBlockMemoryTracer(new GethTraceOptions { EnableMemory = true }, _specProvider),
                    DumpOptions.Geth);
            }

            processedBlocks = null;
        }
        finally
        {
            if (invalidBlockHash is not null)
            {
                DeleteInvalidBlocks(in processingBranch, invalidBlockHash);
            }
        }

        return processedBlocks;
    }

    private ProcessingBranch PrepareProcessingBranch(Block suggestedBlock, ProcessingOptions options)
    {
        if (!options.ContainsFlag(ProcessingOptions.IgnoreParentNotOnMainChain))
        {
            return _branchBuilder.PrepareProcessingBranch(suggestedBlock, options);
        }

        // Engine API newPayload processes the block directly on its parent without collecting a branch.
        BlockHeader? parent = suggestedBlock.IsGenesis ? null : _blockTree.FindParentHeader(suggestedBlock.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        ArrayPoolList<Block> blocks = new(1);
        if (!options.ContainsFlag(ProcessingOptions.ForceProcessing)) blocks.Add(suggestedBlock);
        return new ProcessingBranch(parent, blocks);
    }

    public async ValueTask DisposeAsync()
    {
        _stats.NewProcessingStatistics -= OnNewProcessingStatistics;
        _blockTree.NewBestSuggestedBlock -= OnNewBestBlock;
        _blockTree.NewHeadBlock -= OnNewHeadBlock;
        await StopAsync(processRemainingBlocks: false);
    }

    public class Options
    {
        public static Options Default = new();

        public bool StoreReceiptsByDefault { get; set; } = true;

        public DumpOptions DumpOptions { get; set; } = DumpOptions.None;
    }
}
