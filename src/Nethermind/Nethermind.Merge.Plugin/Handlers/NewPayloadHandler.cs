// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// Provides an execution payload handler as defined in Engine API
/// <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/shanghai.md#engine_newpayloadv2">
/// Shanghai</a> specification.
/// </summary>
public sealed class NewPayloadHandler : IAsyncHandler<ExecutionPayload, PayloadStatusV1>, IDisposable
{
    private readonly IPayloadPreparationService _payloadPreparationService;
    private readonly IBlockValidator _blockValidator;
    private readonly IBlockTree _blockTree;
    private readonly IPoSSwitcher _poSSwitcher;
    private readonly IBeaconSyncStrategy _beaconSyncStrategy;
    private readonly IBeaconPivot _beaconPivot;
    private readonly IBlockCacheService _blockCacheService;
    private readonly IBlockProcessingQueue _processingQueue;
    private readonly IMergeSyncController _mergeSyncController;
    private readonly IInvalidChainTracker _invalidChainTracker;
    private readonly IStateReader _stateReader;
    private readonly ISpecProvider _specProvider;
    private readonly ITxValidator _txValidator;
    private readonly RecoverSignatures _senderRecovery;
    private readonly ILogger _logger;
    private readonly LruCache<Hash256AsKey, CachedPayloadResult>? _latestBlocks;
    private readonly ProcessingOptions _defaultProcessingOptions;
    private readonly TimeSpan _timeout;

    private readonly ConcurrentDictionary<Hash256, ValidationCompletion> _blockValidationTasks = new();

    private ulong _lastBlockNumber;
    private ulong _lastBlockGasLimit;
    private readonly bool _simulateBlockProduction;

    public NewPayloadHandler(
        IPayloadPreparationService payloadPreparationService,
        IBlockValidator blockValidator,
        IBlockTree blockTree,
        IPoSSwitcher poSSwitcher,
        IBeaconSyncStrategy beaconSyncStrategy,
        IBeaconPivot beaconPivot,
        IBlockCacheService blockCacheService,
        IBlockProcessingQueue processingQueue,
        IInvalidChainTracker invalidChainTracker,
        IMergeSyncController mergeSyncController,
        IMergeConfig mergeConfig,
        IReceiptConfig receiptConfig,
        IStateReader stateReader,
        RecoverSignatures senderRecovery,
        ISpecProvider specProvider,
        ITxValidator txValidator,
        ILogManager logManager)
    {
        _payloadPreparationService = payloadPreparationService;
        _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
        _blockTree = blockTree;
        _poSSwitcher = poSSwitcher;
        _beaconSyncStrategy = beaconSyncStrategy;
        _beaconPivot = beaconPivot;
        _blockCacheService = blockCacheService;
        _processingQueue = processingQueue;
        _invalidChainTracker = invalidChainTracker;
        _mergeSyncController = mergeSyncController;
        _stateReader = stateReader;
        _specProvider = specProvider;
        _txValidator = txValidator;
        _senderRecovery = senderRecovery;
        _logger = logManager.GetClassLogger<NewPayloadHandler>();
        _defaultProcessingOptions = receiptConfig.StoreReceipts ? ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts : ProcessingOptions.EthereumMerge;
        _timeout = TimeSpan.FromMilliseconds(mergeConfig.NewPayloadBlockProcessingTimeout);
        if (mergeConfig.NewPayloadCacheSize > 0)
            _latestBlocks = new(mergeConfig.NewPayloadCacheSize, 0, "LatestBlocks");
        _simulateBlockProduction = mergeConfig.SimulateBlockProduction;
        _processingQueue.BlockExecuted += GetProcessingQueueOnBlockExecuted;
        _processingQueue.BlockRemoved += GetProcessingQueueOnBlockRemoved;
    }

    private string GetGasChange(ulong blockGasLimit) => blockGasLimit.CompareTo(_lastBlockGasLimit) switch
    {
        > 0 => "👆",
        < 0 => "👇",
        _ => "  "
    };

    /// <summary>
    /// Processes the execution payload and returns the <see cref="PayloadStatusV1"/>
    /// and the hash of the last valid block.
    /// </summary>
    /// <param name="request">The execution payload to process.</param>
    /// <returns></returns>
    public async Task<ResultWrapper<PayloadStatusV1>> HandleAsync(ExecutionPayload request)
    {
        // Every wait this request takes comes out of one budget, taken here.
        long deadline = Stopwatch.GetTimestamp() + (long)(_timeout.TotalSeconds * Stopwatch.Frequency);

        // Overlaps ecrecover with everything that follows, block processing included; the pipeline
        // recovers inline whatever it reaches before the background recovery does.
        StartSenderRecovery(request);

        Result<Block> decodingResult = request.TryGetBlock(_poSSwitcher.FinalTotalDifficulty);
        if (decodingResult.IsError)
        {
            if (_logger.IsTrace) _logger.Trace($"New Block Request Invalid: {decodingResult.Error} ; {request}.");
            return NewPayloadV1Result.Invalid(null, $"Block {request} could not be parsed as a block: {decodingResult.Error}");
        }
        Block block = decodingResult.Data;

        string requestStr = $"New Block:  {request}";
        if (_logger.IsInfo)
        {
            _logger.Info($"Received {requestStr}      | limit {block.Header.GasLimit,13:N0} {GetGasChange(block.Number == _lastBlockNumber + 1 ? block.Header.GasLimit : _lastBlockGasLimit)} | {block.ParsedExtraData()}");
            _lastBlockNumber = block.Number;
            _lastBlockGasLimit = block.Header.GasLimit;
        }

        // This gate is the precondition for the later ValidateSuggestedBlock(validateHashes: false) calls: the roots
        // below come from TryGetBlock, which derives them from the payload's own body, so a matching header hash
        // binds the body to the header and the validator need not recompute any of them. See the caveat on
        // IBlockValidator.ValidateSuggestedBlock for payload types that take those roots off the wire instead.
        if (!HeaderValidator.ValidateHash(block!.Header, out Hash256 actualHash))
        {
            if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(block, "invalid block hash"));
            Nethermind.Blockchain.Metrics.BadBlocks++;
            if (block.IsByNethermindNode()) Nethermind.Blockchain.Metrics.BadBlocksByNethermindNodes++;
            // Skip recording bad blocks: unverified hashes can poison tracking,
            // while computed hashes could incorrectly blacklist a valid block.
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
            // Keep full orphan validation because ValidateOrphanedBlock is also used without this handler's hash gate.
            if (!_blockValidator.ValidateOrphanedBlock(block!, out string? error))
            {
                if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(block, $"orphaned block is invalid: {error}"));
                RecordBadBlock(block);
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
            _blockCacheService.TryAddBlock(block);
            return NewPayloadV1Result.Syncing;
        }

        if (_simulateBlockProduction)
        {
            _payloadPreparationService.CancelBlockProduction(parentHeader.GenerateSimulatedPayload()
                .GetPayloadId(parentHeader));
        }

        // we need to check if the head is greater than block.Number. In fast sync we could return Valid to CL without this if
        // An IL is a per-call parameter not bound to block.Hash, so never short-circuit when one is supplied.
        // The canonical marker on its own is too weak to answer from: forward sync and the fast-headers backfill
        // both mark blocks canonical without executing them, and the state a block committed may since have been
        // pruned. A block sync placed there without running it falls through to processing - an inclusion-list
        // payload too, whose answer below reads the state root and so cannot tell the state this block committed
        // from one it merely shares a root with.
        bool isCanonicalBehindHead = _blockTree.IsOnMainChainBehindOrEqualHead(block.Header);
        bool hasInclusionList = HasInclusionList(block);
        if (isCanonicalBehindHead && WasExecuted(block))
        {
            if (!hasInclusionList)
            {
                // A block being re-executed is already marked processed from its first run, so it can be made head
                // while that re-execution is still committing the state it restores. Its own commit is waited for,
                // as the parent's is below, before the state is read a second time.
                if (IsVerdictServiceable(block)
                    || (await WaitForCommitInFlightAsync(block.Hash!, deadline) == true && IsVerdictServiceable(block)))
                {
                    if (_logger.IsInfo) _logger.Info($"Valid... A new payload ignored. Block {block.ToString(Block.Format.Short)} found in main chain.");
                    return NewPayloadV1Result.Valid(block.Hash);
                }

                // Not re-executed: a second run failing a check tightened since acceptance would be handled like any
                // invalid block, deleting this block and every block after it up to the head. The cost is that the
                // head cannot move back here until the state returns.
                if (_logger.IsInfo) _logger.Info($"Syncing... A new payload found in main chain whose state is gone. Block {block.ToString(Block.Format.Short)}.");
                return NewPayloadV1Result.Syncing;
            }

            // Reuse the cached result for this exact (block, IL) so re-validating a known-canonical block
            // whose parent state may be pruned doesn't regress to SYNCING; a different IL falls through.
            if (TryGetCachedResult(block, out ResultWrapper<PayloadStatusV1>? cachedResult))
            {
                if (_logger.IsInfo) _logger.Info($"Valid... A new payload with a known inclusion-list result. Block {block.ToString(Block.Format.Short)} found in main chain.");
                return cachedResult;
            }

            // Compliance depends only on the block, the list and the state the block committed, so a
            // canonical block is answerable from that state alone. Re-executing it instead would replay
            // the whole pruning window whenever a consensus client resends the recent chain.
            if (_stateReader.HasStateForBlock(block.Header))
            {
                if (_logger.IsInfo) _logger.Info($"Valid... A new payload re-checked against its own state. Block {block.ToString(Block.Format.Short)} found in main chain.");
                return EvaluateInclusionListFromState(block);
            }

            // bogota.md engine_newPayloadV6 (2.1) requires a VALID response to carry a compliance answer,
            // and with the block's state pruned there is none to derive.
            if (_logger.IsInfo) _logger.Info($"Syncing... A new payload whose inclusion list is no longer evaluable. Block {block.ToString(Block.Format.Short)} found in main chain.");
            return NewPayloadV1Result.Syncing;
        }

        // The parent may have been answered VALID a moment ago and still be committing: its processed flag and its
        // state land when it leaves the processing queue. Judged before that, this block would be taken for one whose
        // parent we do not have, inserted for beacon sync and answered SYNCING. Nothing in flight returns at once.
        if (!await WaitForParentCommitAsync(parentHeader, deadline)) return NewPayloadV1Result.Syncing;

        if (!ShouldProcessBlock(block, parentHeader, out ProcessingOptions processingOptions)) // we shouldn't process block
        {
            // A block our own chain level already points at is not something to sync towards: inserting it as a
            // beacon block would arm the beacon pivot behind the head, and the lookup below misses the backfilled
            // headers whose bodies were never stored. Nor is it validated first - that would only decide the insert,
            // and a failure is recorded against the chain, so every block descending from it, the head included,
            // would then be answered INVALID.
            if (isCanonicalBehindHead)
            {
                if (_logger.IsInfo) _logger.Info($"Syncing... Block already known in blockTree {block}.");
                return NewPayloadV1Result.Syncing;
            }

            if (!_blockValidator.ValidateSuggestedBlock(block, parentHeader, out string? error, validateHashes: false))
            {
                if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(block, $"suggested block is invalid, {error}"));
                RecordBadBlock(block);
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

        // A canonical block this node never ran reaches here when its parent has state. If the head descends from it,
        // a processing failure would be recorded against the head and delete everything from it up to the head, so
        // it is answered like a block ran onto the node's own chain. Only a stale marker - one the head does not
        // descend from - is processed and, if invalid, recorded.
        if (isCanonicalBehindHead && IsAncestorOfHead(block.Header))
        {
            if (_logger.IsInfo) _logger.Info($"Syncing... A new payload found in main chain that this node never ran. Block {block.ToString(Block.Format.Short)}.");
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

        // Not boosted any more: the block runs on the processing loop's thread, which raises its own priority, and this
        // thread only waits for the verdict - and a boost held across that await would resume on another thread and
        // never be restored.
        (ValidationResult result, string? message) = await ValidateBlockAndProcess(block, parentHeader, processingOptions, deadline);

        switch (result)
        {
            case ValidationResult.Syncing:
                {
                    if (_logger.IsInfo) _logger.Info($"Processing queue wasn't empty added to queue {requestStr}.");
                    return NewPayloadV1Result.Syncing;
                }
            case ValidationResult.Invalid:
                {
                    if (_logger.IsWarn) _logger.Warn(InvalidBlockHelper.GetMessage(block, $"{message}"));
                    _invalidChainTracker.OnInvalidBlock(block.Hash!, block.ParentHash);
                    return ResultWrapper<PayloadStatusV1>.Success(BuildInvalidPayloadStatusV1(request, message));
                }
            case ValidationResult.InclusionListUnsatisfied:
                {
                    if (_logger.IsInfo) _logger.Info($"Inclusion list unsatisfied. Result of {requestStr}.");
                    return NewPayloadV1Result.InclusionListUnsatisfied(block.Hash);
                }
            case ValidationResult.Valid:
                {
                    if (_logger.IsDebug) _logger.Debug($"Valid. Result of {requestStr}.");
                    return NewPayloadV1Result.Valid(block.Hash);
                }
            default:
                return ThrowUnknownValidationResult(result);
        }
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private ResultWrapper<PayloadStatusV1> ThrowUnknownValidationResult(ValidationResult result) =>
        throw new InvalidOperationException($"Unknown validation result {result}.");

    private static bool HasInclusionList(Block block) => block.InclusionListTransactions is { Length: > 0 };

    /// <summary>Whether this node ran <paramref name="block"/> itself, as opposed to only downloading it.</summary>
    /// <remarks>
    /// The canonical marker is not evidence of execution on its own: the fast-headers backfill, forward sync and
    /// era import all place blocks on the main chain without ever running them.
    /// </remarks>
    private bool WasExecuted(Block block) =>
        _blockTree.GetInfo(block.Number, block.Hash!).Info is { WasProcessed: true };

    /// <summary>Whether the current head descends from <paramref name="header"/>.</summary>
    /// <remarks>
    /// The chain-level marker cannot answer this: sync moves it without moving the head, leaving stale markers the
    /// head does not descend from. Walks the head's ancestry down to the header's height, which the only caller
    /// bounds by requiring the header's parent to still have state.
    /// </remarks>
    private bool IsAncestorOfHead(BlockHeader header)
    {
        BlockHeader? current = _blockTree.Head?.Header;
        while (current is not null && current.Number > header.Number)
        {
            current = _blockTree.FindParentHeader(current, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }

        return current?.Hash == header.Hash;
    }

    /// <summary>Whether a VALID verdict for <paramref name="block"/> still describes something this node can act on.</summary>
    /// <remarks>
    /// A VALID answer invites the CL to make the block head and build on it. Either the state the block committed
    /// is still readable, so the node can build the next payload on it, or the block sits below the latest known
    /// finalized block, where the MAY-skip clause of
    /// <see href="https://github.com/ethereum/execution-apis/pull/786">execution-apis#786</see> lets
    /// <c>engine_forkchoiceUpdated</c> answer without moving the head - so it can never become the base for the
    /// next payload and the missing state cannot strand block production.
    /// The answer is not stable within one request: a block that has its verdict but has not committed yet gains
    /// its state part way through, so callers re-read rather than share one result.
    /// </remarks>
    private bool IsVerdictServiceable(Block block) =>
        _stateReader.HasStateForBlock(block.Header) || _blockTree.IsOnMainChainBehindFinalized(block.Header);

    // An absent IL digests to default, matching non-IL cache entries.
    private static ValueHash256 ComputeInclusionListDigest(Block block)
    {
        if (block.InclusionListTransactions is not { Length: > 0 } il) return default;

        using ArrayPoolDisposableReturn _ = ArrayPoolDisposableReturn.Rent(il.Length * Keccak.Size, out byte[] buffer);
        Span<byte> span = buffer.AsSpan(0, il.Length * Keccak.Size);
        for (int i = 0; i < il.Length; i++)
            (il[i].Hash ?? Keccak.Zero).Bytes.CopyTo(span.Slice(i * Keccak.Size, Keccak.Size));

        return ValueKeccak.Compute(span);
    }

    /// <summary>Answers an already-committed block's inclusion-list compliance without re-executing it.</summary>
    /// <remarks>
    /// EIP-7805 appendability is judged against the state the block committed, which for a canonical block
    /// is readable at its own state root, so the only work left is recovering the list's senders.
    /// </remarks>
    private ResultWrapper<PayloadStatusV1> EvaluateInclusionListFromState(Block block)
    {
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);
        _senderRecovery.RecoverData(block.InclusionListTransactions!, spec, skipErrors: true);

        ValidationResult result = InclusionListValidator.IsSatisfied(
            block, new SpecificBlockReadOnlyStateProvider(_stateReader, block.Header), spec, _txValidator)
            ? ValidationResult.Valid
            : ValidationResult.InclusionListUnsatisfied;

        _latestBlocks?.Set(block.GetOrCalculateHash(), new CachedPayloadResult(result, null, ComputeInclusionListDigest(block)));
        return result == ValidationResult.Valid
            ? NewPayloadV1Result.Valid(block.Hash)
            : NewPayloadV1Result.InclusionListUnsatisfied(block.Hash);
    }

    // Only a "valid block" outcome short-circuits: never resurrect a stale Invalid/Syncing for a block
    // the tree treats as canonical.
    private bool TryGetCachedResult(Block block, [NotNullWhen(true)] out ResultWrapper<PayloadStatusV1>? result)
    {
        result = null;
        if (_latestBlocks is null
            || !_latestBlocks.TryGet(block.GetOrCalculateHash(), out CachedPayloadResult cached)
            || cached.InclusionListDigest != ComputeInclusionListDigest(block))
            return false;

        result = cached.Result switch
        {
            ValidationResult.Valid => NewPayloadV1Result.Valid(block.Hash),
            ValidationResult.InclusionListUnsatisfied => NewPayloadV1Result.InclusionListUnsatisfied(block.Hash),
            _ => null
        };
        return result is not null;
    }

    /// <summary>Records a block rejected before <c>BranchProcessor</c> ever runs.</summary>
    /// <remarks>
    /// Mirrors the bookkeeping <see cref="Nethermind.Consensus.Processing.BlockchainProcessor"/> does
    /// when it catches an <c>InvalidBlockException</c>: bumps the bad-block metrics, marks the chain
    /// as invalid in <see cref="IInvalidChainTracker"/>, and forwards the block to the
    /// <c>BadBlockStore</c> so it surfaces in <c>debug_getBadBlocks</c>.
    /// Pre-process rejection sites (failed orphan validation, failed suggested-block
    /// validation) previously skipped these two.
    /// </remarks>
    private void RecordBadBlock(Block block)
    {
        if (block.Hash is null) return;

        Nethermind.Blockchain.Metrics.BadBlocks++;
        if (block.IsByNethermindNode())
        {
            Nethermind.Blockchain.Metrics.BadBlocksByNethermindNodes++;
        }

        _invalidChainTracker.OnInvalidBlock(block.Hash, block.ParentHash);
        _blockTree.ReportBadBlock(block);
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
        bool parentProcessed = parentBlockInfo is { WasProcessed: true } && _stateReader.HasStateForBlock(parent);

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

    /// <summary>Slack above head within which early recovery is worthwhile, mirroring the
    /// few-blocks-to-process window in <see cref="ShouldProcessBlock"/>.</summary>
    private const ulong NearHeadRecoveryDistance = 8;

    private void StartSenderRecovery(ExecutionPayload request)
    {
        // Far-from-tip payloads (beacon/forward sync) take Syncing/insert paths that never use
        // the senders; they recover in the processing queue as before.
        if (request.BlockNumber > (_blockTree.Head?.Number ?? 0) + NearHeadRecoveryDistance)
            return;

        Result<Transaction[]> transactions = request.TryGetTransactions();
        if (transactions.IsError || transactions.Data.Length == 0)
            // TryGetBlock reports the decoding error; nothing to recover otherwise.
            return;

        IReleaseSpec spec = _specProvider.GetSpec(new ForkActivation(request.BlockNumber, request.Timestamp));
        try
        {
            _senderRecovery.StartRecovery(request.BlockHash, transactions.Data, spec);
        }
        catch (Exception e)
        {
            // Best-effort: the processing-queue preprocessor recovers anything still missing, so failing
            // to queue the early recovery must not fail an otherwise valid payload.
            if (_logger.IsDebug) _logger.Debug($"Early sender recovery failed to start for block {request.BlockNumber}: {e}");
        }
    }

    /// <summary>
    /// Waits, within the request's budget, for a parent that is still in the processing queue; <c>false</c> when it
    /// did not leave in time, which is answered SYNCING as an unprocessed parent always was.
    /// </summary>
    private async Task<bool> WaitForParentCommitAsync(BlockHeader parent, long deadline)
    {
        Hash256 parentHash = parent.GetOrCalculateHash();
        if (_blockTree.GetInfo(parent.Number, parentHash).Info is not { WasProcessed: false }) return true;

        bool? committed = await WaitForCommitInFlightAsync(parentHash, deadline);
        if (committed == false && _logger.IsDebug) _logger.Debug($"Parent {parent.ToString(BlockHeader.Format.Short)} did not leave the processing queue within the request's budget. Assume Syncing.");
        return committed != false;
    }

    /// <summary>
    /// Waits, within the request's budget, for a copy of the block that already has its verdict to leave the
    /// processing queue, and with it to commit.
    /// </summary>
    /// <returns><c>null</c> when no such copy is in flight, otherwise whether it left in time.</returns>
    private async Task<bool?> WaitForCommitInFlightAsync(Hash256 blockHash, long deadline)
    {
        Task removed = _processingQueue.WaitUntilRemovedAsync(blockHash, executedOnly: true).AsTask();
        if (removed.IsCompleted) return null;

        using CancellationTokenSource bound = new();
        bool inTime = await Task.WhenAny(removed, Task.Delay(RemainingBudget(deadline), bound.Token)) == removed;
        if (inTime) bound.Cancel();
        return inTime;
    }

    /// <summary>What is left of one request's <see cref="IMergeConfig.NewPayloadBlockProcessingTimeout"/>.</summary>
    /// <remarks>
    /// Every wait a request takes comes out of this one budget, so a payload holds the engine API's lock for that
    /// long whatever it waited on, rather than for the sum of a wait for its parent and a wait for itself.
    /// </remarks>
    private TimeSpan RemainingBudget(long deadline)
    {
        TimeSpan left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private async Task<(ValidationResult, string?)> ValidateBlockAndProcess(Block block, BlockHeader parent, ProcessingOptions processingOptions, long deadline)
    {
        ValueHash256 ilDigest = ComputeInclusionListDigest(block);

        ValidationCompletion? completion = null;

        ValidationResult TryCacheResult(ValidationResult result, string? errorMessage)
        {
            // Cache terminal outcomes only; SYNCING isn't terminal (we haven't processed the block yet).
            if (result is ValidationResult.Invalid or ValidationResult.Valid or ValidationResult.InclusionListUnsatisfied)
            {
                _latestBlocks?.Set(block.GetOrCalculateHash(), new CachedPayloadResult(result, errorMessage, ilDigest));
                // The verdict is given before the commit, so the block can be gone without committing by the time
                // this runs. Whichever of the two marks the completion first, the other takes the entry back out.
                if (completion?.MarkAnswerCached() == false) _latestBlocks?.Delete(block.GetOrCalculateHash());
            }
            return result;
        }

        (ValidationResult? result, string? validationMessage) = (null, null);
        ValidationResult terminalResult = ValidationResult.Syncing;

        // If duplicate, reuse results. Invalidity is permanent, but a cached VALID (or unsatisfied-IL) verdict
        // describes a state this node may no longer hold, so it is only reused while it stays serviceable. An
        // unsatisfied-IL verdict that falls through is re-judged on re-execution, to the same answer.
        if (_latestBlocks is not null
            && _latestBlocks.TryGet(block.Hash!, out CachedPayloadResult cachedResult)
            && cachedResult.InclusionListDigest == ilDigest
            && (cachedResult.Result == ValidationResult.Invalid || IsVerdictServiceable(block)))
        {
            if (cachedResult.Result == ValidationResult.Invalid)
            {
                if (_logger.IsWarn) _logger.Warn("Invalid block found in latestBlock cache.");
            }
            return (cachedResult.Result, cachedResult.Message);
        }

        // Validate
        if (!ValidateWithBlockValidator(block, parent, out validationMessage))
        {
            return (TryCacheResult(ValidationResult.Invalid, validationMessage), validationMessage);
        }

        ValidationCompletion blockProcessed = _blockValidationTasks.GetOrAdd(block.Hash!, static _ => new());
        completion = blockProcessed;

        try
        {
            using CancellationTokenSource cts = new();
            Task timeoutTask = Task.Delay(RemainingBudget(deadline), cts.Token);

            AddBlockResult addResult = await _blockTree.SuggestBlockAsync(block, BlockTreeSuggestOptions.ForceDontSetAsMain).AsTask().TimeoutOn(timeoutTask);

            // A payload sent again while its first copy is between verdict and removal is known, and marked processed
            // only part way through that window. Queued again before the copy is gone it would be skipped as not
            // better than head and answered INVALID, or answered by the copy's removal without its own inclusion
            // list ever judged, so let the first copy finish first. Only a copy that has its verdict is worth waiting
            // for; one that is merely queued is left to answer this request through the shared completion, as before,
            // and a copy already gone costs nothing here.
            if (addResult == AddBlockResult.AlreadyKnown)
            {
                Task removed = _processingQueue.WaitUntilRemovedAsync(block.Hash!, executedOnly: true).AsTask();
                if (await Task.WhenAny(removed, timeoutTask) == timeoutTask) throw new TimeoutException();
                // The first copy's own verdict and removal land on whatever completion is registered for the hash,
                // so if they consumed this one it must not stand in for the answer to this request. A fault is that
                // copy's failure, and stands: the CL's next retry re-processes.
                if (blockProcessed.Task.IsFaulted) await blockProcessed.Task;
                if (blockProcessed.Task.IsCompleted)
                {
                    blockProcessed = new();
                    completion = blockProcessed;
                    _blockValidationTasks[block.Hash!] = blockProcessed;
                }
            }

            result = addResult switch
            {
                AddBlockResult.InvalidBlock => ValidationResult.Invalid,
                // if the block is marked as AlreadyKnown by the block tree then it means it has already
                // been suggested. there are three possibilities, either the block hasn't been processed yet,
                // the block was processed and returned invalid but this wasn't saved anywhere or the block was
                // processed and marked as valid.
                // if marked as processed by the block tree then return VALID, otherwise null so that it's processed a few lines below
                // an IL-bearing payload bypasses this shortcut so that the current call's IL is re-validated
                // Re-read rather than reuse the cache check's answer above: SuggestBlockAsync and the wait for a
                // copy in flight sit between the two, and the commit this block is waiting on lands in that gap.
                AddBlockResult.AlreadyKnown => !HasInclusionList(block) && WasExecuted(block) && IsVerdictServiceable(block)
                    ? ValidationResult.Valid
                    : null,
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
                // Off this thread: the processing queue's channel allows synchronous continuations
                // (BlockchainProcessor._blockQueue), so with the queue empty the processor runs the block inside
                // Enqueue, on the caller's thread, and hands it back only once the block is committed - after the
                // verdict this request only needs to see. The processing loop raises its own thread's priority, so
                // nothing is lost by not inheriting this one's. A failure once the block is counted in reaches the
                // request as BlockRemoved(QueueException); one before that leaves the request to its timeout and
                // SYNCING, which the CL retries.
                _ = Task.Run(() => EnqueueAsync(block, processingOptions));
                (result, validationMessage) = await blockProcessed.Task.TimeoutOn(timeoutTask, cts);
            }
            else
            {
                // Already known block with known processing result, cancel the timeout task
                cts.Cancel();
            }
        }
        catch (TimeoutException)
        {
            // we timed out while processing the block, result will be null and we will return SYNCING below, no need to do anything
            if (_logger.IsDebug) _logger.Debug($"Block {block.ToString(Block.Format.FullHashAndNumber)} timed out when processing. Assume Syncing.");
        }
        finally
        {
            // Cached before the completion is dropped, so a block that fails to commit meanwhile still has
            // something to hand the entry back through. Afterwards the removal deletes the entry directly.
            // Dropping it also keeps blocks that exit before the queue publishes BlockRemoved - a timeout, a
            // throw - from pinning their completion in _blockValidationTasks forever.
            terminalResult = TryCacheResult(result ?? ValidationResult.Syncing, validationMessage);
            _blockValidationTasks.TryRemove(block.Hash!, out _);
        }

        return (terminalResult, validationMessage);
    }

    /// <summary>
    /// The verdict, delivered as soon as the block is executed and validated: the commit and the chain update it
    /// still has ahead of it do not change the answer, and the CL's next call waits for them where it has to
    /// (<see cref="IBlockProcessingQueue.WaitUntilRemovedAsync"/>). Any other outcome still comes through
    /// <see cref="GetProcessingQueueOnBlockRemoved"/>, and a verdict already given makes that a no-op.
    /// </summary>
    private void GetProcessingQueueOnBlockExecuted(object? o, BlockHashEventArgs e)
    {
        // Left in place rather than taken: the request has its answer but not its cache entry yet, and a commit
        // that fails next needs the completion to stop that entry from standing.
        if (!_blockValidationTasks.TryGetValue(e.BlockHash, out ValidationCompletion? blockProcessed)) return;

        ValidationResult result = e.ProcessingResult == ProcessingResult.InclusionListUnsatisfied
            ? ValidationResult.InclusionListUnsatisfied
            : ValidationResult.Valid;
        blockProcessed.MarkVerdictGiven();
        blockProcessed.TrySetResult((result, null));
    }

    /// <summary>Whether the tree has the block as processed, so a removal that failed was some other copy's.</summary>
    /// <remarks>
    /// Read only when there is an entry to delete, which is a handful of recently answered blocks, so the header
    /// lookup is not on the path of every skipped block.
    /// </remarks>
    private bool HasCommitted(Hash256 blockHash) =>
        _blockTree.FindHeader(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded) is { Number: ulong number }
        && _blockTree.WasProcessed(number, blockHash);

    private async Task EnqueueAsync(Block block, ProcessingOptions processingOptions)
    {
        try
        {
            await _processingQueue.Enqueue(block, processingOptions);
        }
        catch (Exception e)
        {
            // The queue reports the failure to the request as BlockRemoved(QueueException); this only keeps the
            // exception observed.
            if (_logger.IsDebug) _logger.Debug($"Enqueueing {block.ToString(Block.Format.FullHashAndNumber)} failed: {e}");
        }
    }

    private void GetProcessingQueueOnBlockRemoved(object? o, BlockRemovedEventArgs e)
    {
        // Anything but a block that reached the chain. Which failure it is does not matter once execution has
        // answered: the commit, the prewarm join and the BlockProcessed subscribers all arrive here as an exception,
        // including the one kind that would otherwise have been reported as the block being invalid. Any of them
        // leaves a VALID that was already given standing for a block that is not there.
        bool failed = e.ProcessingResult is not (ProcessingResult.Success or ProcessingResult.InclusionListUnsatisfied);
        bool found = _blockValidationTasks.TryRemove(e.BlockHash, out ValidationCompletion? blockProcessed);

        // The completion that received the verdict arbitrates with its own request over the cache entry, below.
        // Every other shape has to be judged against the cache directly: the request may be done and gone, or a
        // re-submission may have swapped in a completion of its own, which never saw the first copy's verdict and
        // so would let its answer stand. The exception is a second copy skipped as no better than a head the first
        // copy became - that block did commit, and its answer is worth keeping.
        if (failed && (!found || !blockProcessed!.VerdictGiven)
            && _latestBlocks is not null && _latestBlocks.TryGet(e.BlockHash, out _) && !HasCommitted(e.BlockHash))
        {
            _latestBlocks.Delete(e.BlockHash);
        }

        if (!found || blockProcessed is null) return;

        // Still in flight, so this request is between its verdict and its cache write. Whichever of the two marks
        // the completion first, the other takes the entry out.
        if (failed && blockProcessed.VerdictGiven && blockProcessed.MarkBlockUncommitted()) _latestBlocks?.Delete(e.BlockHash);

        if (e.ProcessingResult == ProcessingResult.Exception)
        {
            BlockchainException? exception = new(e.Exception?.Message ?? "Block processing threw exception.", e.Exception);
            blockProcessed.TrySetException(exception);
            return;
        }

        ValidationResult? validationResult = e.ProcessingResult switch
        {
            ProcessingResult.Success => ValidationResult.Valid,
            ProcessingResult.ProcessingError => ValidationResult.Invalid,
            ProcessingResult.InclusionListUnsatisfied => ValidationResult.InclusionListUnsatisfied,
            _ => null
        };

        string? validationMessage = e.ProcessingResult switch
        {
            ProcessingResult.QueueException => "Block cannot be added to processing queue.",
            ProcessingResult.MissingBlock => "Block wasn't found in tree.",
            ProcessingResult.ProcessingError => e.Message ?? "Block processing failed.",
            _ => null
        };

        blockProcessed.TrySetResult((validationResult, validationMessage));
    }

    private bool ValidateWithBlockValidator(Block block, BlockHeader parent, out string? error)
    {
        block.Header.TotalDifficulty ??= parent.TotalDifficulty + block.Difficulty;
        block.Header.IsPostMerge = true; // I think we don't need to set it again here.
        bool isValid = _blockValidator.ValidateSuggestedBlock(block, parent, out error, validateHashes: false);
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
                _blockCacheService.TryAddBlock(block);
                return false;
            }

            while (stack.TryPop(out Block? child))
            {
                _blockTree.Insert(child, BlockTreeInsertBlockOptions.SaveHeader, insertHeaderOptions);
                _blockCacheService.TryRemoveBlock(child.Hash!);
            }

            _beaconPivot.ProcessDestination = block.Header;
        }

        return true;
    }

    public void Dispose()
    {
        _processingQueue.BlockExecuted -= GetProcessingQueueOnBlockExecuted;
        _processingQueue.BlockRemoved -= GetProcessingQueueOnBlockRemoved;
    }

    internal enum ValidationResult
    {
        Invalid,
        Valid,
        Syncing,
        InclusionListUnsatisfied
    }

    // The IL digest disambiguates a resubmission of the same block with a different, per-call IL.
    /// <summary>One request's completion, and the arbiter of whether its answer may stay cached.</summary>
    /// <remarks>
    /// The verdict reaches the request before the block is committed, so a commit that then fails races the
    /// request's own cache write. Both mark here, and whichever arrives second finds the other's mark and deletes
    /// the entry, so an uncommitted block never leaves a terminal answer for the next request to be answered from.
    /// </remarks>
    private sealed class ValidationCompletion()
        : TaskCompletionSource<(ValidationResult? validationResult, string? validationMessage)>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        private const int Cached = 1;
        private const int Uncommitted = 2;
        private int _state;
        private volatile bool _verdictGiven;

        /// <summary>Whether execution already answered this request, so anything else the removal says is a failure
        /// that came after it.</summary>
        public bool VerdictGiven => _verdictGiven;

        /// <summary>Records that execution has answered this request.</summary>
        public void MarkVerdictGiven() => _verdictGiven = true;

        /// <summary>Marks the answer as cached.</summary>
        /// <returns><c>false</c> when the block has already failed to commit, so the entry must be deleted.</returns>
        public bool MarkAnswerCached() => Interlocked.Exchange(ref _state, Cached) != Uncommitted;

        /// <summary>Marks the block as gone without committing.</summary>
        /// <returns><c>true</c> when the answer is already cached, so the entry must be deleted.</returns>
        public bool MarkBlockUncommitted() => Interlocked.Exchange(ref _state, Uncommitted) == Cached;
    }

    private readonly record struct CachedPayloadResult(ValidationResult Result, string? Message, ValueHash256 InclusionListDigest);
}
