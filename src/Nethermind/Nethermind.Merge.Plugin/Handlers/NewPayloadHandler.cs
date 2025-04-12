// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// Provides an execution payload handler as defined in Engine API
/// <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/shanghai.md#engine_newpayloadv2">
/// Shanghai</a> specification.
/// </summary>
public class NewPayloadHandler : IAsyncHandler<ExecutionPayload, PayloadStatusV1>
{
    private readonly IBlockValidator _blockValidator;
    private readonly IBlockTree _blockTree;
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IBeaconSyncStrategy _beaconSyncStrategy;
    private readonly IBeaconPivot _beaconPivot;
    private readonly IBlockCacheService _blockCacheService;
    private readonly IBlockProcessingQueue _processingQueue;
    private readonly IMergeSyncController _mergeSyncController;
    private readonly IInvalidChainTracker _invalidChainTracker;
    private readonly ILogger _logger;
    private readonly LruCache<ValueHash256, (bool valid, string? message)>? _latestBlocks;
    private readonly ProcessingOptions _defaultProcessingOptions;
    private readonly TimeSpan _timeout;

    private long _lastBlockNumber;
    private long _lastBlockGasLimit;

    public NewPayloadHandler(
        IBlockValidator blockValidator,
        IBlockTree blockTree,
        IPoSSwitcher poSSwitcher,
        IBeaconSyncStrategy beaconSyncStrategy,
        IBeaconPivot beaconPivot,
        IBlockCacheService blockCacheService,
        IBlockProcessingQueue processingQueue,
        IInvalidChainTracker invalidChainTracker,
        IMergeSyncController mergeSyncController,
        ILogManager logManager,
        TimeSpan? timeout = null,
        bool storeReceipts = true,
        int cacheSize = 50)
    {
        _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
        _blockTree = blockTree;
        _poSSwitcher = poSSwitcher;
        _beaconSyncStrategy = beaconSyncStrategy;
        _beaconPivot = beaconPivot;
        _blockCacheService = blockCacheService;
        _processingQueue = processingQueue;
        _invalidChainTracker = invalidChainTracker;
        _mergeSyncController = mergeSyncController;
        _logger = logManager.GetClassLogger();
        _defaultProcessingOptions = storeReceipts ? ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts : ProcessingOptions.EthereumMerge;
        _timeout = timeout ?? TimeSpan.FromSeconds(7);
        if (cacheSize > 0)
            _latestBlocks = new(cacheSize, 0, "LatestBlocks");
    }

    public event EventHandler<BlockHeader>? NewPayloadForParentReceived;

    private string GetGasChange(long blockGasLimit)
    {
        return (blockGasLimit - _lastBlockGasLimit) switch
        {
            > 0 => "👆",
            < 0 => "👇",
            _ => "  "
        };
    }

    /// <summary>
    /// Processes the execution payload and returns the <see cref="PayloadStatusV1"/>
    /// and the hash of the last valid block.
    /// </summary>
    /// <param name="request">The execution payload to process.</param>
    /// <returns></returns>
    public async Task<ResultWrapper<PayloadStatusV1>> HandleAsync(ExecutionPayload request)
    {
        BlockDecodingResult decodingResult = request.TryGetBlock(_poSSwitcher.FinalTotalDifficulty);
        Block? block = decodingResult.Block;
        if (block is null)
        {
            if (_logger.IsTrace) _logger.Trace($"New Block Request Invalid: {decodingResult.Error} ; {request}.");
            return NewPayloadV1Result.Invalid(null, $"Block {request} could not be parsed as a block: {decodingResult.Error}");
        }

        string requestStr = $"New Block:  {request}";
        if (_logger.IsInfo)
        {
            _logger.Info($"Received {requestStr}      | limit {block.Header.GasLimit,13:N0} {GetGasChange(block.Number == _lastBlockNumber + 1 ? block.Header.GasLimit : _lastBlockGasLimit)} | {block.ParsedExtraData()}");
            _lastBlockNumber = block.Number;
            _lastBlockGasLimit = block.Header.GasLimit;
        }

        if (!HeaderValidator.ValidateHash(block!.Header, out Hash256 actualHash))
        {
            if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(block, "invalid block hash"));
            return NewPayloadV1Result.Invalid(null, $"Invalid block hash {request.BlockHash} does not match calculated hash {actualHash}.");
        }

        _invalidChainTracker.SetChildParent(block.Hash!, block.ParentHash!);
        if (_invalidChainTracker.IsOnKnownInvalidChain(block.Hash!, out Hash256? lastValidHash))
        {
            if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(block, $"block is a part of an invalid chain") + $". The last valid is {lastValidHash}");
            return NewPayloadV1Result.Invalid(lastValidHash, $"Block {request} is known to be a part of an invalid chain.");
        }

        // Imagine that node was on block X and later node was offline.
        // Now user download new Nethermind release with sync pivot X+100 and start the node.
        // Without hasNeverBeenInSync check user won't be able to catch up with the chain,
        // because blocks would be ignored with this check:
        // block.Header.Number <= _syncConfig.PivotNumberParsed
        bool hasNeverBeenInSync = (_blockTree.Head?.Number ?? 0) == 0;
        if (hasNeverBeenInSync && block.Header.Number <= _blockTree.SyncPivot.BlockNumber)
        {
            if (_logger.IsInfo) _logger.Info($"Pre-pivot block, ignored and returned Syncing. Result of {requestStr}.");
            return NewPayloadV1Result.Syncing;
        }

        block.Header.TotalDifficulty = _poSSwitcher.FinalTotalDifficulty;

        BlockHeader? parentHeader = _blockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (parentHeader is null)
        {
            if (!_blockValidator.ValidateOrphanedBlock(block!, out string? error))
            {
                if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(block, "orphaned block is invalid"));
                return NewPayloadV1Result.Invalid(null, $"Invalid block without parent: {error}.");
            }

            // possible that headers sync finished before this was called, so blocks in cache weren't inserted
            if (!_beaconSyncStrategy.IsBeaconSyncFinished(parentHeader))
            {
                bool inserted = TryInsertDanglingBlock(block);
                if (_logger.IsInfo) _logger.Info(inserted ? $"BeaconSync not finished - block {block} inserted" : $"BeaconSync not finished - block {block} added to cache.");
                return NewPayloadV1Result.Syncing;
            }

            if (_logger.IsInfo) _logger.Info($"Insert block into cache without parent {block}");
            _blockCacheService.BlockCache.TryAdd(block.Hash!, block);
            return NewPayloadV1Result.Syncing;
        }

        NewPayloadForParentReceived?.Invoke(this, parentHeader);

        // we need to check if the head is greater than block.Number. In fast sync we could return Valid to CL without this if
        if (_blockTree.IsOnMainChainBehindOrEqualHead(block))
        {
            if (_logger.IsInfo) _logger.Info($"Valid... A new payload ignored. Block {block.ToString(Block.Format.Short)} found in main chain.");
            return NewPayloadV1Result.Valid(block.Hash);
        }

        if (!ShouldProcessBlock(block, parentHeader, out ProcessingOptions processingOptions)) // we shouldn't process block
        {
            if (!_blockValidator.ValidateSuggestedBlock(block, out string? error, validateHashes: false))
            {
                if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(block, $"suggested block is invalid, {error}"));
                return NewPayloadV1Result.Invalid(error);
            }

            BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.BeaconBlockInsert;

            if (block.Number <= Math.Max(_blockTree.BestKnownNumber, _blockTree.BestKnownBeaconNumber) && _blockTree.FindBlock(block.GetOrCalculateHash(), BlockTreeLookupOptions.TotalDifficultyNotNeeded) is not null)
            {
                if (_logger.IsInfo) _logger.Info($"Syncing... Block already known in blockTree {block}.");
                return NewPayloadV1Result.Syncing;
            }

            if (_beaconPivot.ProcessDestination is not null && _beaconPivot.ProcessDestination.Hash == block.ParentHash)
            {
                insertHeaderOptions |= BlockTreeInsertHeaderOptions.MoveToBeaconMainChain; // we're extending our beacon canonical chain
                _beaconPivot.ProcessDestination = block.Header;
            }

            _beaconPivot.EnsurePivot(block.Header, true);
            _blockTree.Insert(block, BlockTreeInsertBlockOptions.SaveHeader | BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks, insertHeaderOptions);

            if (_logger.IsInfo) _logger.Info($"Syncing... Inserting block {block}.");
            return NewPayloadV1Result.Syncing;
        }

        if (_poSSwitcher.MisconfiguredTerminalTotalDifficulty())
        {
            const string errorMessage = "Misconfigured terminal total difficulty.";
            if (_logger.IsWarn) _logger.Warn(errorMessage);
            return NewPayloadV1Result.Invalid(Keccak.Zero, errorMessage);
        }

        if ((block.TotalDifficulty ?? 0) != 0 && _poSSwitcher.BlockBeforeTerminalTotalDifficulty(parentHeader))
        {
            string errorMessage = $"Invalid terminal block. Nethermind TTD {_poSSwitcher.TerminalTotalDifficulty}, Parent TD: {parentHeader.TotalDifficulty}. Request: {requestStr}.";
            if (_logger.IsWarn) _logger.Warn(errorMessage);

            // {status: INVALID, latestValidHash: 0x0000000000000000000000000000000000000000000000000000000000000000, validationError: errorMessage | null} if terminal block conditions are not satisfied
            return NewPayloadV1Result.Invalid(Keccak.Zero, errorMessage);
        }

        // Otherwise, we can just process this block and we don't need to do BeaconSync anymore.
        _mergeSyncController.StopSyncing();

        using var handle = Thread.CurrentThread.BoostPriority();
        // Try to execute block
        (ValidationResult result, string? message) = await ValidateBlockAndProcess(block, parentHeader, processingOptions);

        if (result == ValidationResult.Invalid)
        {
            if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(block, $"{message}"));
            _invalidChainTracker.OnInvalidBlock(block.Hash!, block.ParentHash);
            return ResultWrapper<PayloadStatusV1>.Success(BuildInvalidPayloadStatusV1(request, message));
        }

        if (result == ValidationResult.Syncing)
        {
            if (_logger.IsInfo) _logger.Info($"Processing queue wasn't empty added to queue {requestStr}.");
            return NewPayloadV1Result.Syncing;
        }

        if (_logger.IsDebug) _logger.Debug($"Valid. Result of {requestStr}.");
        return NewPayloadV1Result.Valid(block.Hash);
    }

    /// <summary>
    /// Decides if we should process the block or try syncing to it. It also returns what options to process the block with.
    /// </summary>
    /// <param name="block">Block</param>
    /// <param name="parent">Parent header</param>
    /// <param name="processingOptions">Options that should be used for processing</param>
    /// <returns>Options which should be used for block processing. Null if we shouldn't process the block.</returns>
    /// <remarks>
    /// We decide to process blocks in two situations:
    /// 1. The block parent was already processed. Then we process with ProcessingOptions.EthereumMerge with potentially also StoringReceipts.
    ///    This contains ProcessingOptions.IgnoreParentNotOnMainChain flag in order not to collect whole branch for processing, but only process this block directly on parent.
    ///    As parent was processed the state to process on should also be available.
    ///
    /// 2. If the parent wasn't processed, but it was a PoW block (terminal block) and we are not syncing PoW chain and are in the deep past.
    ///    In this case we remove ~ProcessingOptions.IgnoreParentNotOnMainChain flag in order to collect whole branch for processing.
    ///    If we didn't support this edge case then we couldn't process this block and would have to return Syncing, which is not desired during transition.
    ///
    /// Scenario 2 proved to be quite common on testnets which produced multiple transition blocks.
    /// </remarks>
    private bool ShouldProcessBlock(Block block, BlockHeader parent, out ProcessingOptions processingOptions)
    {
        processingOptions = _defaultProcessingOptions;

        BlockInfo? parentBlockInfo = _blockTree.GetInfo(parent.Number, parent.GetOrCalculateHash()).Info;
        bool parentProcessed = parentBlockInfo is { WasProcessed: true };

        // During the transition we can have a case of NP built over a transition block that wasn't processed.
        // We want to force process the whole branch then, but not longer than few blocks.
        // But we don't want this to trigger when we are in beacon sync.
        // The last condition: !parentBlockInfo.IsBeaconInfo will be true for terminal blocks.
        // Checking _posSwitcher.IsTerminal might not be the best, because we're loading parentHeader with DoNotCalculateTotalDifficulty option
        bool weHaveOnlyFewBlocksToProcess = (_blockTree.Head?.Number ?? 0) + 8 >= block.Number;
        bool parentIsPoWBlock = parent.Difficulty != UInt256.Zero;
        bool processTerminalBlock = !_poSSwitcher.TransitionFinished // we haven't finished transition
                                    && weHaveOnlyFewBlocksToProcess // we won't try to process too much blocks (if we are behind the transition block and still processing blocks)
                                    && parentBlockInfo is { IsBeaconInfo: false } // we are not in beacon sync
                                    && parentIsPoWBlock; // parent was PoW block -> so it was a transition block

        if (!parentProcessed && processTerminalBlock) // so if parent wasn't processed
        {
            if (_logger.IsInfo) _logger.Info($"Forced processing block {block}, block TD: {block.TotalDifficulty}, parent: {parent}, parent TD: {parent.TotalDifficulty}");

            // if parent wasn't processed and we want to force processing terminal block then we need to allow to process whole branch, not just one block
            // in all other cases when parent is processed ProcessingOptions.IgnoreParentNotOnMainChain allows us to process just this block ignoring that its not on Head
            // this option is part of ProcessingOptions.EthereumMerge option
            processingOptions &= ~ProcessingOptions.IgnoreParentNotOnMainChain;
        }

        return parentProcessed || processTerminalBlock;
    }

    private async Task<(ValidationResult, string?)> ValidateBlockAndProcess(Block block, BlockHeader parent, ProcessingOptions processingOptions)
    {
        ValidationResult TryCacheResult(ValidationResult result, string? errorMessage)
        {
            // notice that it is not correct to add information to the cache
            // if we return SYNCING for example, and don't know yet whether
            // the block is valid or invalid because we haven't processed it yet
            if (result == ValidationResult.Valid || result == ValidationResult.Invalid)
                _latestBlocks?.Set(block.GetOrCalculateHash(), (result == ValidationResult.Valid, errorMessage));
            return result;
        }

        (ValidationResult? result, string? validationMessage) = (null, null);

        // If duplicate, reuse results
        if (_latestBlocks is not null && _latestBlocks.TryGet(block.Hash!, out (bool valid, string? message) cachedResult))
        {
            (bool isValid, string? message) = cachedResult;
            if (!isValid)
            {
                if (_logger.IsWarn) _logger.Warn("Invalid block found in latestBlock cache.");
            }
            return (isValid ? ValidationResult.Valid : ValidationResult.Invalid, message);
        }

        // Validate
        if (!ValidateWithBlockValidator(block, parent, out validationMessage))
        {
            return (TryCacheResult(ValidationResult.Invalid, validationMessage), validationMessage);
        }

        TaskCompletionSource<ValidationResult?> blockProcessedTaskCompletionSource = new();
        Task<ValidationResult?> blockProcessed = blockProcessedTaskCompletionSource.Task;

        void GetProcessingQueueOnBlockRemoved(object? o, BlockRemovedEventArgs e)
        {
            if (e.BlockHash == block.Hash)
            {
                _processingQueue.BlockRemoved -= GetProcessingQueueOnBlockRemoved;

                if (e.ProcessingResult == ProcessingResult.Exception)
                {
                    BlockchainException? exception = new(e.Exception?.Message ?? "Block processing threw exception.", e.Exception);
                    blockProcessedTaskCompletionSource.SetException(exception);
                    return;
                }

                ValidationResult? validationResult = e.ProcessingResult switch
                {
                    ProcessingResult.Success => ValidationResult.Valid,
                    ProcessingResult.ProcessingError => ValidationResult.Invalid,
                    _ => null
                };

                validationMessage = e.ProcessingResult switch
                {
                    ProcessingResult.QueueException => "Block cannot be added to processing queue.",
                    ProcessingResult.MissingBlock => "Block wasn't found in tree.",
                    ProcessingResult.ProcessingError => e.Message ?? "Block processing failed.",
                    _ => null
                };

                blockProcessedTaskCompletionSource.TrySetResult(validationResult);
            }
        }

        _processingQueue.BlockRemoved += GetProcessingQueueOnBlockRemoved;
        try
        {
            Task timeoutTask = Task.Delay(_timeout);

            AddBlockResult addResult = await _blockTree
                .SuggestBlockAsync(block, BlockTreeSuggestOptions.ForceDontSetAsMain)
                .AsTask().TimeoutOn(timeoutTask);

            result = addResult switch
            {
                AddBlockResult.InvalidBlock => ValidationResult.Invalid,
                // if the block is marked as AlreadyKnown by the block tree then it means it has already
                // been suggested. there are three possibilities, either the block hasn't been processed yet,
                // the block was processed and returned invalid but this wasn't saved anywhere or the block was
                // processed and marked as valid.
                // if marked as processed by the blocktree then return VALID, otherwise null so that it's process a few lines below
                AddBlockResult.AlreadyKnown => _blockTree.WasProcessed(block.Number, block.Hash!) ? ValidationResult.Valid : null,
                _ => null
            };

            validationMessage = addResult switch
            {
                AddBlockResult.InvalidBlock => "Block couldn't be added to the tree.",
                AddBlockResult.AlreadyKnown => "Block was already known in the tree.",
                _ => null
            };

            if (!result.HasValue)
            {
                // we don't know the result of processing the block, either because
                // it is the first time we add it to the tree or it's AlreadyKnown in
                // the tree but hasn't yet been processed. if it's the second case
                // probably the block is already in the processing queue as a result
                // of a previous newPayload or the block being discovered during syncing
                // but add it to the processing queue just in case.
                _processingQueue.Enqueue(block, processingOptions);
                result = await blockProcessed.TimeoutOn(timeoutTask);
            }
        }
        catch (TimeoutException)
        {
            // we timed out while processing the block, result will be null and we will return SYNCING below, no need to do anything
            if (_logger.IsDebug) _logger.Debug($"Block {block.ToString(Block.Format.FullHashAndNumber)} timed out when processing. Assume Syncing.");
        }
        finally
        {
            _processingQueue.BlockRemoved -= GetProcessingQueueOnBlockRemoved;
        }

        return (TryCacheResult(result ?? ValidationResult.Syncing, validationMessage), validationMessage);
    }

    private bool ValidateWithBlockValidator(Block block, BlockHeader parent, out string? error)
    {
        block.Header.TotalDifficulty ??= parent.TotalDifficulty + block.Difficulty;
        block.Header.IsPostMerge = true; // I think we don't need to set it again here.
        bool isValid = _blockValidator.ValidateSuggestedBlock(block, out error, validateHashes: false);
        if (!isValid && _logger.IsWarn) _logger.Warn($"Block validator rejected the block {block.ToString(Block.Format.FullHashAndNumber)}.");
        return isValid;
    }

    private PayloadStatusV1 BuildInvalidPayloadStatusV1(ExecutionPayload request, string? validationMessage) =>
        new()
        {
            Status = PayloadStatus.Invalid,
            ValidationError = validationMessage,
            LatestValidHash = _invalidChainTracker.IsOnKnownInvalidChain(request.BlockHash!, out Hash256? lastValidHash)
                ? lastValidHash
                : request.ParentHash
        };

    /// Pop blocks from cache up to ancestor on the beacon chain. Which is then inserted into the block tree
    /// which I assume will switch the canonical chain.
    /// Return false if no ancestor that is part of beacon chain found.
    private bool TryInsertDanglingBlock(Block block)
    {
        BlockTreeInsertHeaderOptions insertHeaderOptions = BlockTreeInsertHeaderOptions.BeaconBlockInsert | BlockTreeInsertHeaderOptions.MoveToBeaconMainChain;

        if (!_blockTree.IsKnownBeaconBlock(block.Number, block.Hash ?? block.CalculateHash()))
        {
            // last block inserted is parent of current block, part of the same chain
            Block? current = block;
            Stack<Block> stack = new();
            while (current is not null)
            {
                stack.Push(current);
                Hash256 currentHash = current.Hash!;
                if (currentHash == _beaconPivot.PivotHash || _blockTree.IsKnownBeaconBlock(current.Number, currentHash))
                {
                    break;
                }

                _blockCacheService.BlockCache.TryGetValue(current.ParentHash!, out Block? parentBlock);
                current = parentBlock;
            }

            if (current is null)
            {
                // block not part of beacon pivot chain, save in cache
                _blockCacheService.BlockCache.TryAdd(block.Hash!, block);
                return false;
            }

            while (stack.TryPop(out Block? child))
            {
                _blockTree.Insert(child, BlockTreeInsertBlockOptions.SaveHeader, insertHeaderOptions);
            }

            _beaconPivot.ProcessDestination = block.Header;
        }

        return true;
    }

    private enum ValidationResult
    {
        Invalid,
        Valid,
        Syncing
    }
}
