// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Metrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Consensus.Processing;

public sealed class BlockchainProcessor : IBlockchainProcessor, IBlockProcessingQueue
{
    public int SoftMaxRecoveryQueueSizeInTx = 10000; // adjust based on tx or gas
    public const int MaxProcessingQueueSize = 2048; // adjust based on tx or gas

    private static readonly AsyncLocal<bool> _isMainProcessingThread = new();
    public static bool IsMainProcessingThread => _isMainProcessingThread.Value;
    public bool IsMainProcessor { get; init; }

    public ITracerBag Tracers => _compositeBlockTracer;

    private readonly IBlockProcessor _blockProcessor;
    private readonly IBlockPreprocessorStep _recoveryStep;
    private readonly IStateReader _stateReader;
    private readonly Options _options;
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
            // Optimize for single writer concurrency (recovery queue)
            SingleWriter = true,
        });

    private bool _recoveryComplete = false;
    private int _queueCount;

    private readonly ProcessingStats _stats;

    private CancellationTokenSource? _loopCancellationSource;
    private Task? _recoveryTask;
    private Task? _processorTask;
    private DateTime _lastProcessedBlock;

    private int _currentRecoveryQueueSize;
    private bool _isProcessingBlock;
    private const int MaxBranchSize = 8192;
    private readonly CompositeBlockTracer _compositeBlockTracer = new();
    private readonly Stopwatch _stopwatch = new();

    public event EventHandler<IBlockchainProcessor.InvalidBlockEventArgs>? InvalidBlock;

    /// <summary>
    ///
    /// </summary>
    /// <param name="blockTree"></param>
    /// <param name="blockProcessor"></param>
    /// <param name="recoveryStep"></param>
    /// <param name="stateReader"></param>
    /// <param name="logManager"></param>
    /// <param name="options"></param>
    public BlockchainProcessor(
        IBlockTree? blockTree,
        IBlockProcessor? blockProcessor,
        IBlockPreprocessorStep? recoveryStep,
        IStateReader stateReader,
        ILogManager? logManager,
        Options options)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
        _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
        _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        _options = options;

        _blockTree.NewBestSuggestedBlock += OnNewBestBlock;
        _blockTree.NewHeadBlock += OnNewHeadBlock;

        _stats = new ProcessingStats(stateReader, _logger);
        _loopCancellationSource = new CancellationTokenSource();
    }

    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        _lastProcessedBlock = DateTime.UtcNow;
    }

    private void OnNewBestBlock(object sender, BlockEventArgs blockEventArgs)
    {
        ProcessingOptions options = ProcessingOptions.None;
        if (_options.StoreReceiptsByDefault)
        {
            options |= ProcessingOptions.StoreReceipts;
        }

        if (blockEventArgs.Block is not null)
        {
            Enqueue(blockEventArgs.Block, options);
        }
    }

    public void Enqueue(Block block, ProcessingOptions processingOptions)
    {
        if (_logger.IsTrace) _logger.Trace($"Enqueuing a new block {block.ToString(Block.Format.Short)} for processing.");

        int currentRecoveryQueueSize = Interlocked.Add(ref _currentRecoveryQueueSize, block.Transactions.Length);
        Hash256? blockHash = block.Hash!;
        BlockRef blockRef = currentRecoveryQueueSize >= SoftMaxRecoveryQueueSizeInTx
            ? new BlockRef(blockHash, processingOptions)
            : new BlockRef(block, processingOptions);

        if (!_recoveryComplete)
        {
            Interlocked.Increment(ref _queueCount);
            try
            {
                _recoveryQueue.Writer.TryWrite(blockRef);
                if (_logger.IsTrace) _logger.Trace($"A new block {block.ToString(Block.Format.Short)} enqueued for processing.");
            }
            catch (Exception e)
            {
                Interlocked.Decrement(ref _queueCount);
                BlockRemoved?.Invoke(this, new BlockRemovedEventArgs(blockHash, ProcessingResult.QueueException, e));
                if (e is not InvalidOperationException || !_recoveryComplete)
                {
                    throw;
                }
            }
        }
    }

    public void Start()
    {
        _loopCancellationSource ??= new CancellationTokenSource();
        _recoveryTask = RunRecovery();
        _processorTask = RunProcessing();
    }

    public async Task StopAsync(bool processRemainingBlocks = false)
    {
        _recoveryComplete = true;
        if (processRemainingBlocks)
        {
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

        await Task.WhenAll(_recoveryTask ?? Task.CompletedTask, _processorTask ?? Task.CompletedTask);
        if (_logger.IsInfo) _logger.Info("Blockchain Processor shutdown complete.. please wait for all components to close");
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

    private async Task RunRecoveryLoop()
    {
        void DecrementQueue(Hash256 blockHash, ProcessingResult processingResult, Exception? exception = null)
        {
            Interlocked.Decrement(ref _queueCount);
            BlockRemoved?.Invoke(this, new BlockRemovedEventArgs(blockHash, processingResult, exception));
            FireProcessingQueueEmpty();
        }

        if (_logger.IsDebug) _logger.Debug($"Starting recovery loop - {_blockQueue.Reader.Count} blocks waiting in the queue.");
        _lastProcessedBlock = DateTime.UtcNow;
        await foreach (BlockRef blockRef in _recoveryQueue.Reader.ReadAllAsync(CancellationToken))
        {
            try
            {
                if (blockRef.Resolve(_blockTree))
                {
                    Interlocked.Add(ref _currentRecoveryQueueSize, -blockRef.Block!.Transactions.Length);
                    if (_logger.IsTrace) _logger.Trace($"Recovering addresses for block {blockRef.BlockHash}.");
                    _recoveryStep.RecoverData(blockRef.Block);

                    try
                    {
                        await _blockQueue.Writer.WriteAsync(blockRef);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        DecrementQueue(blockRef.BlockHash, ProcessingResult.QueueException, e);

                        if (e is InvalidOperationException)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Recovery loop stopping.");
                            return;
                        }

                        throw;
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
                DecrementQueue(blockRef.BlockHash, ProcessingResult.Exception, e);
                throw;
            }
        }
    }

    private CancellationToken CancellationToken
        => _loopCancellationSource?.Token ?? CancellationTokenExtensions.AlreadyCancelledToken;

    private async Task RunProcessing()
    {
        _isMainProcessingThread.Value = IsMainProcessor;

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

    private async Task RunProcessingLoop()
    {
        if (_logger.IsDebug) _logger.Debug($"Starting block processor - {_blockQueue.Reader.Count} blocks waiting in the queue.");

        FireProcessingQueueEmpty();

        GCScheduler.Instance.SwitchOnBackgroundGC(0);
        await foreach (BlockRef blockRef in _blockQueue.Reader.ReadAllAsync(CancellationToken))
        {
            using var handle = Thread.CurrentThread.BoostPriorityHighest();
            // Have block, switch off background GC timer
            GCScheduler.Instance.SwitchOffBackgroundGC(_blockQueue.Reader.Count);
            _isProcessingBlock = true;
            try
            {
                if (blockRef.IsInDb || blockRef.Block is null)
                {
                    BlockRemoved?.Invoke(this, new BlockRemovedEventArgs(blockRef.BlockHash, ProcessingResult.MissingBlock));
                    throw new InvalidOperationException("Block processing expects only resolved blocks");
                }

                Block block = blockRef.Block;

                if (_logger.IsTrace) _logger.Trace($"Processing block {block.ToString(Block.Format.Short)}).");
                _stats.Start();
                Block processedBlock = Process(block, blockRef.ProcessingOptions, _compositeBlockTracer.GetTracer(), CancellationToken, out string? error);

                if (processedBlock is null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Failed / skipped processing {block.ToString(Block.Format.Full)}");
                    BlockRemoved?.Invoke(this, new BlockRemovedEventArgs(blockRef.BlockHash, ProcessingResult.ProcessingError, error));
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Processed block {block.ToString(Block.Format.Full)}");
                    BlockRemoved?.Invoke(this, new BlockRemovedEventArgs(blockRef.BlockHash, ProcessingResult.Success));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (_logger.IsWarn) _logger.Warn($"Processing block failed. Block: {blockRef}, Exception: {exception}");
                BlockRemoved?.Invoke(this, new BlockRemovedEventArgs(blockRef.BlockHash, ProcessingResult.Exception, exception));
            }
            finally
            {
                _isProcessingBlock = false;
                Interlocked.Decrement(ref _queueCount);
            }

            if (_logger.IsTrace) _logger.Trace($"Now {_blockQueue.Reader.Count} blocks waiting in the queue.");
            FireProcessingQueueEmpty();

            GCScheduler.Instance.SwitchOnBackgroundGC(_blockQueue.Reader.Count);
        }

        if (_logger.IsInfo) _logger.Info("Block processor queue stopped.");
    }

    private void FireProcessingQueueEmpty()
    {
        if (((IBlockProcessingQueue)this).IsEmpty)
        {
            ProcessingQueueEmpty?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ProcessingQueueEmpty;
    public event EventHandler<BlockRemovedEventArgs>? BlockRemoved;

    int IBlockProcessingQueue.Count => _queueCount;

    public Block? Process(Block suggestedBlock, ProcessingOptions options, IBlockTracer tracer, CancellationToken token = default) =>
        Process(suggestedBlock, options, tracer, token, out _);

    public Block? Process(Block suggestedBlock, ProcessingOptions options, IBlockTracer tracer, CancellationToken token, out string? error)
    {
        error = null;
        if (!RunSimpleChecksAheadOfProcessing(suggestedBlock, options))
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

        bool readonlyChain = options.ContainsFlag(ProcessingOptions.ReadOnlyChain);
        if (!readonlyChain) _stats.CaptureStartStats();

        using ProcessingBranch processingBranch = PrepareProcessingBranch(suggestedBlock, options);
        PrepareBlocksToProcess(suggestedBlock, options, processingBranch);

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

        if (!readonlyChain)
        {
            long blockProcessingTimeInMicrosecs = _stopwatch.ElapsedMicroseconds();
            Metrics.LastBlockProcessingTimeInMs = blockProcessingTimeInMicrosecs / 1000;
            int blockQueueCount = _blockQueue.Reader.Count;
            Metrics.RecoveryQueueSize = Math.Max(_queueCount - blockQueueCount - (_isProcessingBlock ? 1 : 0), 0);
            Metrics.ProcessingQueueSize = blockQueueCount;
            _stats.UpdateStats(lastProcessed, processingBranch.Root, blockProcessingTimeInMicrosecs);
        }

        bool updateHead = !options.ContainsFlag(ProcessingOptions.DoNotUpdateHead);
        if (updateHead)
        {
            if (_logger.IsTrace) _logger.Trace($"Updating main chain: {lastProcessed}, blocks count: {processedBlocks.Length}");
            _blockTree.UpdateMainChain(processingBranch.Blocks, true);
        }

        if ((options & ProcessingOptions.MarkAsProcessed) == ProcessingOptions.MarkAsProcessed)
        {
            if (_logger.IsTrace) _logger.Trace($"Marked blocks as processed {lastProcessed}, blocks count: {processedBlocks.Length}");
            _blockTree.MarkChainAsProcessed(processingBranch.Blocks);
        }

        if (!readonlyChain)
        {
            Metrics.BestKnownBlockNumber = _blockTree.BestKnownNumber;
        }

        return lastProcessed;
    }

    public bool IsProcessingBlocks(ulong? maxProcessingInterval) =>
        _processorTask?.IsCompleted == false && _recoveryTask?.IsCompleted == false &&
        // user does not setup interval and we cannot set interval time based on chainspec
        (maxProcessingInterval is null || _lastProcessedBlock.AddSeconds(maxProcessingInterval.Value) > DateTime.UtcNow);

    private void TraceFailingBranch(in ProcessingBranch processingBranch, ProcessingOptions options, IBlockTracer blockTracer, DumpOptions dumpType)
    {
        if ((_options.DumpOptions & dumpType) != 0)
        {
            try
            {
                _blockProcessor.Process(
                    processingBranch.Root,
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
                BlockTraceDumper.LogTraceFailure(blockTracer, processingBranch.Root, ex, _logger);
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
            processedBlocks = _blockProcessor.Process(
                processingBranch.Root,
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
                InvalidBlock?.Invoke(this, new IBlockchainProcessor.InvalidBlockEventArgs { InvalidBlock = invalidBlock, });

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
                    new GethLikeBlockMemoryTracer(new GethTraceOptions { EnableMemory = true }),
                    DumpOptions.Geth);
            }

            processedBlocks = null;
        }
        finally
        {
            if (invalidBlockHash is not null && !options.ContainsFlag(ProcessingOptions.ReadOnlyChain))
            {
                DeleteInvalidBlocks(in processingBranch, invalidBlockHash);
            }
        }

        return processedBlocks;
    }

    private void PrepareBlocksToProcess(Block suggestedBlock, ProcessingOptions options, ProcessingBranch processingBranch)
    {
        ArrayPoolList<Block> blocksToProcess = processingBranch.BlocksToProcess;
        if (options.ContainsFlag(ProcessingOptions.ForceProcessing))
        {
            processingBranch.Blocks.Clear(); // TODO: investigate why if we clear it all we need to collect and iterate on all the blocks in PrepareProcessingBranch?
            blocksToProcess.Add(suggestedBlock);
        }
        else
        {
            foreach (Block block in processingBranch.Blocks.AsSpan())
            {
                CancellationToken.ThrowIfCancellationRequested();

                if (block.Hash is not null && _blockTree.WasProcessed(block.Number, block.Hash))
                {
                    if (_logger.IsInfo) _logger.Info($"Rerunning block after reorg or pruning: {block.ToString(Block.Format.Short)}");
                }

                blocksToProcess.Add(block);
            }

            Block firstBlock = blocksToProcess[0];
            if (!firstBlock.IsGenesis)
            {
                BlockHeader? parentOfFirstBlock = _blockTree.FindHeader(firstBlock.ParentHash!, BlockTreeLookupOptions.None) ?? throw new InvalidBlockException(firstBlock, $"Rejected a block from a different fork: {firstBlock.ToString(Block.Format.FullHashAndNumber)}");
                if (!_stateReader.HasStateForBlock(parentOfFirstBlock))
                {
                    ThrowOrphanedBlock(firstBlock);
                }
            }
        }

        if (_logger.IsTrace) TraceProcessingBlocks(processingBranch, blocksToProcess);

        for (int i = 0; i < blocksToProcess.Count; i++)
        {
            /* this can happen if the block was loaded as an ancestor and did not go through the recovery queue */
            _recoveryStep.RecoverData(blocksToProcess[i]);
        }

        // Uncommon logging and throws

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceProcessingBlocks(ProcessingBranch processingBranch, ArrayPoolList<Block> blocksToProcess)
            => _logger.Trace($"Processing {blocksToProcess.Count} blocks from state root {processingBranch.Root}");

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowOrphanedBlock(Block firstBlock)
            => throw new InvalidBlockException(firstBlock, $"Rejected a block that is orphaned: {firstBlock.ToString(Block.Format.FullHashAndNumber)}");

    }

    private ProcessingBranch PrepareProcessingBranch(Block suggestedBlock, ProcessingOptions options)
    {
        BlockHeader branchingPoint = null;
        ArrayPoolList<Block> blocksToBeAddedToMain = new((int)Reorganization.PersistenceInterval);

        bool branchingCondition;

        Block toBeProcessed = suggestedBlock;
        long iterations = 0;
        bool isTrace = _logger.IsTrace;
        do
        {
            iterations++;
            if (iterations > MaxBranchSize)
            {
                ThrowMaxBranchSizeReached();
            }

            if (!options.ContainsFlag(ProcessingOptions.Trace))
            {
                blocksToBeAddedToMain.Add(toBeProcessed);
            }

            if (isTrace) TraceProcessingBlock(suggestedBlock, toBeProcessed);
            if (toBeProcessed.IsGenesis)
            {
                break;
            }

            branchingPoint = options.ContainsFlag(ProcessingOptions.ForceSameBlock)
                ? toBeProcessed.Header
                : _blockTree.FindParentHeader(toBeProcessed.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);

            if (branchingPoint is null)
            {
                // genesis block
                break;
            }

            if (options.ContainsFlag(ProcessingOptions.IgnoreParentNotOnMainChain))
            {
                break;
            }

            if (isTrace) TraceParentSearch(toBeProcessed);

            toBeProcessed = _blockTree.FindParent(toBeProcessed.Header, BlockTreeLookupOptions.None);

            if (isTrace) TraceParentBlock(toBeProcessed);

            if (toBeProcessed is null)
            {
                if (_logger.IsDebug) DebugParentNotFound(suggestedBlock);
                break;
            }

            // generally if we finish fast sync at block, e.g. 8 and then have 6 blocks processed and close Neth
            // then on restart we would find 14 as the branch head (since 14 is on the main chain)
            // we need to dig deeper to go all the way to the false (reorg boundary) head
            // otherwise some nodes would be missing
            // we also need to go deeper if we already pruned state for that block
            bool notFoundTheBranchingPointYet = !_blockTree.IsMainChain(branchingPoint.Hash!);
            bool hasState = toBeProcessed?.StateRoot is null || _stateReader.HasStateForBlock(toBeProcessed.Header);
            bool notInForceProcessing = !options.ContainsFlag(ProcessingOptions.ForceProcessing);
            branchingCondition =
                (notFoundTheBranchingPointYet || !hasState)
                && notInForceProcessing;

            if (isTrace) TraceBranchingConditions(branchingPoint, notFoundTheBranchingPointYet, hasState, notInForceProcessing);

        } while (branchingCondition);

        if (isTrace)
        {
            TraceBranchingPoint(branchingPoint);
        }

        Hash256 stateRoot = branchingPoint?.StateRoot;
        if (isTrace) TraceStateRootLookup(stateRoot);

        if (blocksToBeAddedToMain.Count > 1)
            blocksToBeAddedToMain.Reverse();

        return new ProcessingBranch(stateRoot, blocksToBeAddedToMain);

        // Uncommon logging and throws

        [MethodImpl(MethodImplOptions.NoInlining)]
        void DebugParentNotFound(Block suggestedBlock)
            => _logger.Debug($"Treating this as fast sync transition for {suggestedBlock.ToString(Block.Format.Short)}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceBranchingConditions(BlockHeader branchingPoint, bool notFoundTheBranchingPointYet, bool hasState, bool notInForceProcessing)
        {
            _logger.Trace(
                $" Current branching point: " +
                $"{branchingPoint.Number}," +
                $" {branchingPoint.Hash} " +
                $"TD: {branchingPoint.TotalDifficulty} " +
                $"Processing conditions " +
                $"notFoundTheBranchingPointYet {notFoundTheBranchingPointYet}, " +
                $"hasState: {hasState}, " +
                $"notInForceProcessing: {notInForceProcessing}, ");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceBranchingPoint(BlockHeader branchingPoint)
        {
            if (branchingPoint is not null && branchingPoint.Hash != _blockTree.Head?.Hash)
            {
                _logger.Trace($"Head block was: {_blockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}");
                _logger.Trace($"Branching from: {branchingPoint.ToString(BlockHeader.Format.Short)}");
            }
            else
            {
                _logger.Trace(branchingPoint is null ? "Setting as genesis block" : $"Adding on top of {branchingPoint.ToString(BlockHeader.Format.Short)}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceProcessingBlock(Block suggestedBlock, Block toBeProcessed)
            => _logger.Trace($"To be processed (of {suggestedBlock.ToString(Block.Format.Short)}) is {toBeProcessed?.ToString(Block.Format.Short)}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceParentSearch(Block toBeProcessed)
            => _logger.Trace($"Finding parent of {toBeProcessed.ToString(Block.Format.Short)}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceParentBlock(Block toBeProcessed)
            => _logger.Trace($"Found parent {toBeProcessed?.ToString(Block.Format.Short)}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceStateRootLookup(Hash256 stateRoot)
            => _logger.Trace($"State root lookup: {stateRoot}");

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowMaxBranchSizeReached()
            => throw new InvalidOperationException($"Maximum size of branch reached ({MaxBranchSize}). This is unexpected.");
    }

    [Todo(Improve.Refactor, "This probably can be made conditional (in DEBUG only)")]
    private bool RunSimpleChecksAheadOfProcessing(Block suggestedBlock, ProcessingOptions options)
    {
        /* a bit hacky way to get the invalid branch out of the processing loop */
        if (suggestedBlock.Number != 0 &&
            !_blockTree.IsKnownBlock(suggestedBlock.Number - 1, suggestedBlock.ParentHash))
        {
            if (_logger.IsDebug) LogUnknownParentBlock(suggestedBlock);
            return false;
        }

        if (suggestedBlock.Header.TotalDifficulty is null)
        {
            ThrowUnknownTotalDifficulty(suggestedBlock);
        }

        if (!options.ContainsFlag(ProcessingOptions.NoValidation) && suggestedBlock.Hash is null)
        {
            ThrowUnknownBlockHash(suggestedBlock);
        }

        BlockHeader[] uncles = suggestedBlock.Uncles;
        for (int i = 0; i < uncles.Length; i++)
        {
            if (uncles[i].Hash is null)
            {
                ThrowUnknownUncleHash(suggestedBlock, i);
            }
        }

        return true;

        // Uncommon logging and throws

        [MethodImpl(MethodImplOptions.NoInlining)]
        void LogUnknownParentBlock(Block suggestedBlock)
            => _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} with unknown parent");

        [DoesNotReturn]
        [StackTraceHidden]
        void ThrowUnknownTotalDifficulty(Block suggestedBlock)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} without total difficulty");
            throw new InvalidOperationException("Block without total difficulty calculated was suggested for processing");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        void ThrowUnknownBlockHash(Block suggestedBlock)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} without calculated hash");
            throw new InvalidOperationException("Block hash should be known at this stage if running in a validating mode");
        }

        [DoesNotReturn]
        [StackTraceHidden]
        void ThrowUnknownUncleHash(Block suggestedBlock, int i)
        {
            if (_logger.IsDebug) _logger.Debug($"Skipping processing block {suggestedBlock.ToString(Block.Format.FullHashAndNumber)} with null uncle hash ar {i}");
            throw new InvalidOperationException($"Uncle's {i} hash is null when processing block");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _blockTree.NewBestSuggestedBlock -= OnNewBestBlock;
        _blockTree.NewHeadBlock -= OnNewHeadBlock;
        await StopAsync(processRemainingBlocks: false);
    }

    [DebuggerDisplay("Root: {Root}, Length: {BlocksToProcess.Count}")]
    private readonly ref struct ProcessingBranch(Hash256 root, ArrayPoolList<Block> blocks)
    {
        public Hash256 Root { get; } = root;
        public ArrayPoolList<Block> Blocks { get; } = blocks;
        public ArrayPoolList<Block> BlocksToProcess { get; } = new(blocks.Count);

        public void Dispose()
        {
            Blocks.Dispose();
            BlocksToProcess.Dispose();
        }
    }

    public class Options
    {
        public static Options NoReceipts = new() { StoreReceiptsByDefault = true };
        public static Options Default = new();

        public bool StoreReceiptsByDefault { get; set; } = true;

        public DumpOptions DumpOptions { get; set; } = DumpOptions.None;
    }
}
