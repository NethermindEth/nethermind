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
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// Provides an execution payload handler as defined in Engine API
/// <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/shanghai.md#engine_newpayloadv2">
/// Shanghai</a> specification.
/// </summary>
public sealed class NewPayloadHandler : IAsyncHandler<ExecutionPayload, PayloadStatusV1>, IInclusionListComplianceEvaluator, IDisposable
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
    private readonly IBlockAccessListStore? _blockAccessListStore;
    private readonly ISpecProvider _specProvider;
    private readonly ITxValidator _txValidator;
    private readonly ulong _focilProfile2MaxVerifyGas;
    private readonly IProfile2EligibilityReplayer? _profile2Replayer;
    private readonly RecoverSignatures _senderRecovery;
    private readonly ILogger _logger;
    private readonly LruCache<Hash256AsKey, CachedPayloadResult>? _latestBlocks;
    private readonly ProcessingOptions _defaultProcessingOptions;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;

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
        IBlocksConfig blocksConfig,
        ILogManager logManager,
        IProfile2EligibilityReplayer? profile2Replayer = null,
        TimeProvider? timeProvider = null,
        IBlockAccessListStore? blockAccessListStore = null)
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
        _blockAccessListStore = blockAccessListStore;
        _specProvider = specProvider;
        _txValidator = txValidator;
        _focilProfile2MaxVerifyGas = blocksConfig.FocilProfile2MaxVerifyGas;
        _profile2Replayer = profile2Replayer;
        _senderRecovery = senderRecovery;
        _logger = logManager.GetClassLogger<NewPayloadHandler>();
        _defaultProcessingOptions = receiptConfig.StoreReceipts ? ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts : ProcessingOptions.EthereumMerge;
        _timeout = TimeSpan.FromMilliseconds(mergeConfig.NewPayloadBlockProcessingTimeout);
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        long deadline = _timeProvider.GetTimestamp() + (long)(_timeout.TotalSeconds * _timeProvider.TimestampFrequency);

        using ExecutionPayloadPreparation preparation = new(request);
        using CancellationTokenSource senderRecoveryCancellation = new();
        Result<Block> decodingResult;
        using (preparation.Workers.Enter())
        {
            // Started before TryGetBlock so recovery overlaps the background transactions-root job it waits for.
            StartSenderRecovery(request, senderRecoveryCancellation.Token);
            decodingResult = preparation.TryGetBlock(_poSSwitcher.FinalTotalDifficulty);
        }
        if (decodingResult.IsError)
        {
            senderRecoveryCancellation.Cancel();
            if (_logger.IsTrace) _logger.Trace($"New Block Request Invalid: {decodingResult.Error} ; {request}.");
            return NewPayloadV1Result.Invalid(null, $"Block {request} could not be parsed as a block: {decodingResult.Error}");
        }
        Block block = decodingResult.Data;
        ParallelUnbalancedWork.WorkerGroup workers = preparation.Workers;

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
            senderRecoveryCancellation.Cancel();
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
            // A queued ancestor can still fail execution and move latestValidHash backwards.
            // Let an ancestor already answered VALID finish committing within this request's budget.
            if (lastValidHash is not null && !_processingQueue.WaitUntilRemovedAsync(lastValidHash).IsCompleted)
            {
                bool hasExecutedCopy = await _processingQueue.WaitForExecutedCopyAsync(lastValidHash, RemainingBudget(deadline), _timeProvider);
                if (!hasExecutedCopy || !_processingQueue.WaitUntilRemovedAsync(lastValidHash).IsCompleted)
                    return NewPayloadV1Result.Syncing;
            }

            // Queue removal publishes invalidation before completing, so refresh the earlier snapshot.
            _invalidChainTracker.IsOnKnownInvalidChain(block.Hash!, out lastValidHash);
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
        // Only a block this node executed is answered from the canonical marker.
        bool isCanonicalBehindHead = _blockTree.IsOnMainChainBehindOrEqualHead(block.Header);
        if (isCanonicalBehindHead && WasExecuted(block))
        {
            // A re-executed block is already marked processed, so it can become head before its restored state
            // commits; wait for that commit before reading the state again.
            if (IsVerdictServiceable(block)
                || (await _processingQueue.WaitForExecutedCopyAsync(block.Hash!, RemainingBudget(deadline), _timeProvider) && IsVerdictServiceable(block)))
            {
                if (_logger.IsInfo) _logger.Info($"Valid... A new payload ignored. Block {block.ToString(Block.Format.Short)} found in main chain.");
                return NewPayloadV1Result.Valid(block.Hash);
            }

            // Fall through: the ancestry check below decides whether re-execution is safe.
        }

        // The parent may have been answered VALID a moment ago and still be committing: its processed flag and its
        // state land when it leaves the processing queue. Judged before that, this block would be taken for one whose
        // parent we do not have, inserted for beacon sync and answered SYNCING. Nothing in flight returns at once.
        if (!await WaitForParentCommitAsync(parentHeader, deadline)) return NewPayloadV1Result.Syncing;

        if (!ShouldProcessBlock(block, parentHeader, out ProcessingOptions processingOptions)) // we shouldn't process block
        {
            // Inserting a canonical block as a beacon block would arm the beacon pivot behind the head, and
            // validating it first could record a failure that marks every descendant, the head included, INVALID.
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

        // A failed re-execution of a head ancestor would mark the chain invalid and delete blocks up to the head,
        // so only a stale marker proven outside that ancestry is processed.
        if (isCanonicalBehindHead && IsAncestorOfHead(block.Header) is not false)
        {
            if (_logger.IsInfo) _logger.Info($"Syncing... A new payload found in main chain that cannot safely be re-executed. Block {block.ToString(Block.Format.Short)}.");
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
        (ValidationResult result, string? message) = await ValidateBlockAndProcess(block, parentHeader, processingOptions, deadline, workers);

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

    /// <summary>Whether this node executed <paramref name="block"/>, rather than sync placing it on the main chain unrun.</summary>
    private bool WasExecuted(Block block) =>
        _blockTree.GetInfo(block.Number, block.Hash!).Info is { WasProcessed: true };

    /// <summary>Whether the head descends from <paramref name="header"/>, or null if a bounded walk cannot tell.</summary>
    /// <remarks>Sync can move the canonical marker without moving the head, so the marker alone cannot answer this.</remarks>
    private bool? IsAncestorOfHead(BlockHeader header)
    {
        BlockHeader? current = _blockTree.Head?.Header;
        if (current is null || current.Number < header.Number) return null;

        // An archive node can have parent state arbitrarily far behind head.
        const ulong maxAncestorLookupDepth = 128;
        if (current.Number - header.Number > maxAncestorLookupDepth) return null;

        while (current is not null && current.Number > header.Number)
        {
            current = _blockTree.FindParentHeader(current, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        }

        return current is null ? null : current.Hash == header.Hash;
    }

    /// <summary>Whether a VALID verdict for <paramref name="block"/> is still actionable: its state is readable or it is below finalized.</summary>
    /// <remarks>
    /// Below finalized, forkchoiceUpdated may skip the block (<see href="https://github.com/ethereum/execution-apis/pull/786">execution-apis#786</see>),
    /// so it never becomes a payload base. A commit can land mid-request, so callers re-read rather than cache this.
    /// </remarks>
    private bool IsVerdictServiceable(Block block) =>
        _stateReader.HasStateForBlock(block.Header) || _blockTree.IsOnMainChainBehindFinalized(block.Header);

    /// <inheritdoc/>
    /// <remarks>
    /// <c>VALID</c> is answered before the block's state commits, so the check first waits for its executed copy to
    /// leave the processing queue. EIP-7805 judges appendability at the transaction boundary: the state the block
    /// committed, read at its own state root, with the validated BAL reversing finalization's account changes and a
    /// field with no earlier BAL value read from the parent state. Omission claims are fit against the gas its
    /// execution recorded. Null when the BAL or parent state is unavailable.
    /// </remarks>
    public async Task<bool?> EvaluateAsync(Block block, Transaction[][] inclusionLists, OmissionClaim[] claims, CancellationToken cancellationToken)
    {
        Hash256 hash = block.GetOrCalculateHash();
        if (!_stateReader.HasStateForBlock(block.Header))
        {
            await _processingQueue.WaitUntilExecutedCopyRemovedAsync(hash).AsTask().WaitAsync(cancellationToken);
            if (!_stateReader.HasStateForBlock(block.Header)) return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // A decoded payload has no EIP-8037 dimensions, and neither the state root nor the header's
        // max(execution, state) carries them back, so without what execution recorded this would answer on a
        // coarser gas rule than execution and could report real censorship as absent.
        block.Header.GasUsedPerDimension = RecordedGasDimensions(hash);
        block.GasUsedBeforeTransactions = RecordedGasUsedBefore(hash);
        return IsInclusionListSatisfied(block, inclusionLists, claims);
    }

    /// <remarks>A claim before the end of the payload is fit against the gas execution recorded before its index. Once
    /// that record is gone it is judged at both bounds of that gas, none and the whole block's, and answered only when
    /// they agree. Fitting more can only condemn more, so an unsatisfied verdict at the whole block's gas stands.</remarks>
    private bool? IsInclusionListSatisfied(Block block, Transaction[][] inclusionLists, OmissionClaim[] claims)
    {
        IReleaseSpec spec = _specProvider.GetSpec(block.Header);
        _senderRecovery.RecoverData(InclusionLists.Distinct(inclusionLists), spec, skipErrors: true);

        InclusionListCommittedState committed = new(new SpecificBlockReadOnlyStateProvider(_stateReader, block.Header),
            (uint)block.Transactions.Length + 1, ReadList, ReadParent);
        IReadOnlyStateProvider state = committed;
        ulong transactionCount = (ulong)block.Transactions.Length;
        bool earlyClaims = Eip8369Profile2.IsActive(spec)
            && Array.Exists(claims, claim => claim.EvaluationIndex < transactionCount);
        if (earlyClaims && block.GasUsedBeforeTransactions is { } recorded && recorded.Length == block.Transactions.Length)
            return Answer(Evaluate(block, inclusionLists, claims, state, spec, _profile2Replayer, recorded));
        IProfile2EligibilityReplayer? replayer = _profile2Replayer;
        if (earlyClaims)
        {
            state = new CachedAccountStateProvider(state);
            replayer = replayer is null ? null : new RequestReplayer(replayer);
        }

        bool? satisfied = Evaluate(block, inclusionLists, claims, state, spec, replayer, null);
        if (satisfied is not true || !earlyClaims) return Answer(satisfied);

        return Answer(Evaluate(block, inclusionLists, claims, state, spec, replayer, new (ulong, ulong)[block.Transactions.Length]) is true ? true : null);

        bool? Answer(bool? verdict) => committed.Available ? verdict : null;

        ReadOnlyBlockAccessList? ReadList()
        {
            if (block.Header.BlockAccessListHash is not { } commitment) return null;
            if (block.BlockAccessList is { WireHash: { } own } carried && own == commitment) return carried;
            try
            {
                ReadOnlyBlockAccessList? stored = _blockAccessListStore?.Get(block.Number, block.GetOrCalculateHash());
                return stored?.WireHash == commitment ? stored : null;
            }
            catch (RlpException exception)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot recheck inclusion list for {block.Hash}: its BAL does not decode. {exception.Message}");
                return null;
            }
        }

        IReadOnlyStateProvider? ReadParent()
        {
            BlockHeader? parent = _blockTree.FindHeader(block.ParentHash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            return parent is not null && _stateReader.HasStateForBlock(parent)
                ? new SpecificBlockReadOnlyStateProvider(_stateReader, parent) : null;
        }
    }

    private bool? Evaluate(Block block, Transaction[][] inclusionLists, OmissionClaim[] claims, IReadOnlyStateProvider state, IReleaseSpec spec,
        IProfile2EligibilityReplayer? replayer, (ulong Execution, ulong State)[]? gasUsedBefore) =>
        spec.IsEip8037Enabled && block.Header.GasUsedPerDimension is null
            ? EvaluateWithUnknownGasDimensions(block, inclusionLists, claims, state, spec, replayer, gasUsedBefore)
            : InclusionListValidator.IsSatisfied(block, inclusionLists, state, spec, _txValidator, _focilProfile2MaxVerifyGas, claims, replayer, gasUsedBefore);

    /// <summary>Answers only when every possible gas-dimension assignment gives the same verdict.</summary>
    /// <remarks>Account reads are shared across the gas bounds.</remarks>
    private bool? EvaluateWithUnknownGasDimensions(Block block, Transaction[][] inclusionLists, OmissionClaim[] claims, IReadOnlyStateProvider state, IReleaseSpec spec,
        IProfile2EligibilityReplayer? replayer, (ulong Execution, ulong State)[]? gasUsedBefore)
    {
        state = state as CachedAccountStateProvider ?? new CachedAccountStateProvider(state);
        replayer = replayer is null or RequestReplayer ? replayer : new RequestReplayer(replayer);
        // EIP-8037 stores max(execution, state). Appendability decreases as either used dimension increases.
        try
        {
            block.Header.GasUsedPerDimension = (block.GasUsed, block.GasUsed);
            if (!InclusionListValidator.IsSatisfied(block, inclusionLists, state, spec, _txValidator, _focilProfile2MaxVerifyGas, claims, replayer, gasUsedBefore)) return false;

            block.Header.GasUsedPerDimension = (block.GasUsed, 0);
            if (!InclusionListValidator.IsSatisfied(block, inclusionLists, state, spec, _txValidator, _focilProfile2MaxVerifyGas, claims, replayer, gasUsedBefore)) return null;

            block.Header.GasUsedPerDimension = (0, block.GasUsed);
            return InclusionListValidator.IsSatisfied(block, inclusionLists, state, spec, _txValidator, _focilProfile2MaxVerifyGas, claims, replayer, gasUsedBefore) ? true : null;
        }
        finally
        {
            // Bounds must never escape as recorded execution totals.
            block.Header.GasUsedPerDimension = null;
        }
    }

    private sealed class RequestReplayer(IProfile2EligibilityReplayer inner) : IProfile2EligibilityReplayer
    {
        private readonly Dictionary<(Transaction Transaction, int Index), bool> _verdicts = [];

        public bool AreSignaturesValid(Transaction transaction, IReleaseSpec spec) => inner.AreSignaturesValid(transaction, spec);

        public bool[] AreEligible(Block block, IReadOnlyList<(Transaction Transaction, int Index)> requests, IReleaseSpec spec)
        {
            List<(Transaction Transaction, int Index)> missing = [];
            foreach ((Transaction transaction, int index) in requests)
                if (!_verdicts.ContainsKey((transaction, index))) missing.Add((transaction, index));

            if (missing.Count > 0)
            {
                bool[] evaluated = inner.AreEligible(block, missing, spec);
                for (int i = 0; i < missing.Count; i++) _verdicts[missing[i]] = evaluated[i];
            }

            bool[] verdicts = new bool[requests.Count];
            for (int i = 0; i < requests.Count; i++) verdicts[i] = _verdicts[requests[i]];
            return verdicts;
        }
    }

    /// <summary>Shares account reads across one request's inclusion-list checks.</summary>
    private sealed class CachedAccountStateProvider(IReadOnlyStateProvider state) : IReadOnlyStateProvider
    {
        private Dictionary<AddressAsKey, (bool Exists, AccountStruct Account)>? _accounts;

        /// <inheritdoc/>
        public Hash256 StateRoot => state.StateRoot;

        /// <inheritdoc/>
        public bool TryGetAccount(Address address, out AccountStruct account)
        {
            _accounts ??= [];
            if (!_accounts.TryGetValue(address, out (bool Exists, AccountStruct Account) cached))
            {
                bool exists = state.TryGetAccount(address, out account);
                cached = (exists, account);
                _accounts.Add(address, cached);
            }
            account = cached.Account;
            return cached.Exists;
        }

        /// <inheritdoc/>
        public ReadOnlyMemory<byte> GetCode(Address address)
        {
            TryGetAccount(address, out AccountStruct account);
            return !account.HasCode ? Array.Empty<byte>() : state.GetCode(account.CodeHash);
        }

        /// <inheritdoc/>
        public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash) => state.GetCode(in codeHash);

        /// <inheritdoc/>
        public bool IsContract(Address address) => state.IsContract(address);

        /// <inheritdoc/>
        public bool AccountExists(Address address) => state.AccountExists(address);

        /// <inheritdoc/>
        public bool IsDeadAccount(Address address) => state.IsDeadAccount(address);

        /// <inheritdoc/>
        public void Get(in StorageCell storageCell, out UInt256 value) => state.Get(in storageCell, out value);
    }

    /// <summary>The EIP-8037 gas dimensions execution recorded for the block <paramref name="hash"/> names.</summary>
    /// <remarks>Two in-memory copies outlive the payload the dimensions were recorded from — the cached answer and
    /// the tree's own block, which <c>BranchProcessor</c> stamps. Null once neither is left: nothing persists them, and <see cref="BlockHeader.GasUsed"/>
    /// reduces them to a maximum that cannot be inverted.</remarks>
    private (ulong Execution, ulong State)? RecordedGasDimensions(Hash256 hash) =>
        (_latestBlocks is not null && _latestBlocks.TryGet(hash, out CachedPayloadResult cached)
            ? cached.GasUsedPerDimension
            : null)
        ?? _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded)?.GasUsedPerDimension;

    /// <summary>The gas execution recorded the block <paramref name="hash"/> names using before each of its transactions,
    /// kept only by the cached answer.</summary>
    private (ulong Execution, ulong State)[]? RecordedGasUsedBefore(Hash256 hash) =>
        _latestBlocks is not null && _latestBlocks.TryGet(hash, out CachedPayloadResult cached) ? cached.GasUsedBefore : null;

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

    private void StartSenderRecovery(ExecutionPayload request, CancellationToken cancellationToken)
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
            _senderRecovery.StartRecovery(request.BlockHash, transactions.Data, spec, request.Transactions, cancellationToken);
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

        bool inTime = await _processingQueue.WaitForExecutedCopyAsync(parentHash, RemainingBudget(deadline), _timeProvider);
        if (!inTime && _logger.IsDebug) _logger.Debug($"Parent {parent.ToString(BlockHeader.Format.Short)} did not leave the processing queue within the request's budget. Assume Syncing.");
        return inTime;
    }

    /// <summary>What is left of one request's <see cref="IMergeConfig.NewPayloadBlockProcessingTimeout"/>.</summary>
    /// <remarks>
    /// Every wait a request takes comes out of this one budget, so a payload holds the engine API's lock for that
    /// long whatever it waited on, rather than for the sum of a wait for its parent and a wait for itself.
    /// </remarks>
    private TimeSpan RemainingBudget(long deadline)
    {
        TimeSpan left = _timeProvider.GetElapsedTime(_timeProvider.GetTimestamp(), deadline);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private async Task<(ValidationResult, string?)> ValidateBlockAndProcess(Block block, BlockHeader parent, ProcessingOptions processingOptions, long deadline, ParallelUnbalancedWork.WorkerGroup workers)
    {
        ValidationCompletion? completion = null;

        ValidationResult TryCacheResult(ValidationResult result, string? errorMessage)
        {
            // Cache terminal outcomes only; SYNCING isn't terminal (we haven't processed the block yet).
            if (result is ValidationResult.Invalid or ValidationResult.Valid)
            {
                _latestBlocks?.Set(block.GetOrCalculateHash(), new CachedPayloadResult(result, errorMessage, block.Header.GasUsedPerDimension, block.GasUsedBeforeTransactions));
                // The verdict is given before the commit, so the block can be gone without committing by the time
                // this runs. Whichever of the two marks the completion first, the other takes the entry back out.
                if (completion?.MarkAnswerCached() == false) _latestBlocks?.Delete(block.GetOrCalculateHash());
            }
            return result;
        }

        (ValidationResult? result, string? validationMessage) = (null, null);
        ValidationResult terminalResult = ValidationResult.Syncing;

        // If duplicate, reuse results. Invalidity is permanent; any other verdict only while it stays serviceable.
        if (_latestBlocks is not null
            && _latestBlocks.TryGet(block.Hash!, out CachedPayloadResult cachedResult)
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
            Task timeoutTask = Task.Delay(RemainingBudget(deadline), _timeProvider, cts.Token);

            Task<AddBlockResult> suggest = _blockTree.SuggestBlockAsync(block, BlockTreeSuggestOptions.ForceDontSetAsMain).AsTask();
            AddBlockResult addResult;
            try
            {
                addResult = await suggest.TimeoutOn(timeoutTask);
            }
            catch (TimeoutException)
            {
                // The suggest goes on without this request, and a block it adds would otherwise sit in the tree
                // unqueued until the CL re-sends the payload.
                _ = EnqueueOnceAddedAsync(suggest, block, processingOptions, blockProcessed, workers);
                throw;
            }

            // A payload sent again while its first copy is between verdict and removal is known, and marked processed
            // only part way through that window. Queued again before the copy is gone it would be skipped as not
            // better than head and answered INVALID, so let the first copy finish first. Only a copy that has its
            // verdict is worth waiting for; one that is merely queued is left to answer this request through the
            // shared completion, and a copy already gone costs nothing here.
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
                // Re-read serviceability: the commit this block awaits can land after the cache check above.
                AddBlockResult.AlreadyKnown => WasExecuted(block) && IsVerdictServiceable(block)
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
                // nothing is lost by not inheriting this one's. A failure to enqueue fails the request (EnqueueAsync).
                _ = Task.Run(() => EnqueueAsync(block, processingOptions, blockProcessed, workers));
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
    /// (<see cref="IBlockProcessingQueue.WaitUntilExecutedCopyRemovedAsync"/>). Any other outcome still comes through
    /// <see cref="GetProcessingQueueOnBlockRemoved"/>, and a verdict already given makes that a no-op.
    /// </summary>
    private void GetProcessingQueueOnBlockExecuted(object? o, BlockVerdictEventArgs e)
    {
        // Left in place rather than taken: the request has its answer but not its cache entry yet, and a commit
        // that fails next needs the completion to stop that entry from standing.
        if (!_blockValidationTasks.TryGetValue(e.BlockHash, out ValidationCompletion? blockProcessed)) return;

        blockProcessed.MarkVerdictGiven();
        // An unsatisfied inclusion list is no execution failure, and this handler checks inclusion apart from execution.
        if (blockProcessed.TrySetResult((ValidationResult.Valid, null))) e.Answered = true;
    }

    /// <summary>Whether the tree has the block as processed, so a removal that failed was some other copy's.</summary>
    /// <remarks>
    /// Read only when there is an entry to delete, which is a handful of recently answered blocks, so the header
    /// lookup is not on the path of every skipped block.
    /// </remarks>
    private bool HasCommitted(Hash256 blockHash) =>
        _blockTree.FindHeader(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded) is { Number: ulong number }
        && _blockTree.WasProcessed(number, blockHash);

    private async Task EnqueueOnceAddedAsync(Task<AddBlockResult> suggest, Block block, ProcessingOptions processingOptions, ValidationCompletion blockProcessed, ParallelUnbalancedWork.WorkerGroup workers)
    {
        try
        {
            if (await suggest is not AddBlockResult.Added) return;
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Suggesting {block.ToString(Block.Format.FullHashAndNumber)} failed after its request timed out: {e}");
            return;
        }

        // Off the thread that completed the suggest, for the reason the request's own enqueue gives.
        await Task.Run(() => EnqueueAsync(block, processingOptions, blockProcessed, workers));
    }

    private async Task EnqueueAsync(Block block, ProcessingOptions processingOptions, ValidationCompletion blockProcessed, ParallelUnbalancedWork.WorkerGroup workers)
    {
        try
        {
            ValueTask enqueue;
            using (workers.Enter()) enqueue = _processingQueue.Enqueue(block, processingOptions);
            await enqueue;
        }
        catch (Exception e)
        {
            // A failure once the block is counted in reaches the request as BlockRemoved(QueueException), which has
            // completed it already. One before that raises no removal, and the request would otherwise wait out its
            // budget holding the engine API's lock; it fails now instead, as it did when Enqueue ran on its thread. Its
            // own completion, not the hash's: by now a re-sent payload may have registered a fresh one.
            if (_logger.IsDebug) _logger.Debug($"Enqueueing {block.ToString(Block.Format.FullHashAndNumber)} failed: {e}");
            blockProcessed.TrySetException(e);
        }
    }

    private void GetProcessingQueueOnBlockRemoved(object? o, BlockRemovedEventArgs e)
    {
        // Anything but a block that reached the chain. Which failure it is does not matter once execution has
        // answered: the commit, the prewarm join and the BlockProcessed subscribers all arrive here as an exception,
        // including the one kind that would otherwise have been reported as the block being invalid. Any of them
        // leaves a VALID that was already given standing for a block that is not there.
        bool failed = e.ProcessingResult is not ProcessingResult.Success;
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
        Syncing
    }

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

    /// <param name="GasUsedPerDimension">What execution recorded for this block, so a deferred inclusion check
    /// judges appendability on the same EIP-8037 gas execution did.</param>
    /// <param name="GasUsedBefore">The gas execution recorded before each transaction, which an omission claim before
    /// the end of the payload is fit against.</param>
    private readonly record struct CachedPayloadResult(
        ValidationResult Result,
        string? Message,
        (ulong Execution, ulong State)? GasUsedPerDimension,
        (ulong Execution, ulong State)[]? GasUsedBefore);
}
