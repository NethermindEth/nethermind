// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Reporting;
using Nethermind.Facade.Eth.RpcTransaction;
using Autofac.Features.AttributeFilters;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class DebugBridge : IDebugBridge
{
    private readonly BlockTreeMutationLock _mutationLock;
    private readonly IBlockProcessingPauseControl _pauseControl;
    private readonly IBlockProcessingQueue _processingQueue;
    private readonly ILogger _logger;
    private readonly IConfigProvider _configProvider;
    private readonly IGethStyleTracer _tracer;
    private readonly IBlockTree _blockTree;
    private readonly IReceiptStorage _receiptStorage;
    private readonly IReceiptFinder _receiptFinder;
    private readonly IReceiptsMigration _receiptsMigration;
    private readonly ISpecProvider _specProvider;
    private readonly ISyncModeSelector _syncModeSelector;
    private readonly ISyncProgressResolver _syncProgressResolver;
    private readonly ISyncPointers _syncPointers;
    private readonly IBadBlockStore _badBlockStore;
    private readonly IBlockStore _blockStore;
    private readonly IWorldStateManager _worldStateManager;
    private readonly Dictionary<string, IDb> _dbMappings;

    public DebugBridge(
        IConfigProvider configProvider,
        IReadOnlyDbProvider dbProvider,
        IGethStyleTracer tracer,
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        [KeyFilter(IReceiptFinder.RegenerableKey)] IReceiptFinder receiptFinder,
        IReceiptsMigration receiptsMigration,
        ISpecProvider specProvider,
        ISyncModeSelector syncModeSelector,
        IBadBlockStore badBlockStore,
        IBlockStore blockStore,
        IWorldStateManager worldStateManager,
        ILogManager logManager,
        BlockTreeMutationLock mutationLock,
        IBlockProcessingPauseControl pauseControl,
        IBlockProcessingQueue processingQueue,
        ISyncProgressResolver syncProgressResolver,
        ISyncPointers syncPointers)
    {
        _logger = logManager.GetClassLogger<DebugBridge>();
        _mutationLock = mutationLock;
        _pauseControl = pauseControl;
        _processingQueue = processingQueue;
        _syncProgressResolver = syncProgressResolver;
        _syncPointers = syncPointers;
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
        _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
        _receiptsMigration = receiptsMigration ?? throw new ArgumentNullException(nameof(receiptsMigration));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _syncModeSelector = syncModeSelector ?? throw new ArgumentNullException(nameof(syncModeSelector));
        _badBlockStore = badBlockStore;
        // Use the shared singleton store, not a private one over the raw DB, so debug reads observe the
        // deferred-body overlay (a private store would miss a block whose body write is still queued).
        _blockStore = blockStore ?? throw new ArgumentNullException(nameof(blockStore));
        _worldStateManager = worldStateManager ?? throw new ArgumentNullException(nameof(worldStateManager));
        dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
        IDb blockInfosDb = dbProvider.BlockInfosDb ?? throw new ArgumentNullException(nameof(dbProvider.BlockInfosDb));
        IDb headersDb = dbProvider.HeadersDb ?? throw new ArgumentNullException(nameof(dbProvider.HeadersDb));
        IDb codeDb = dbProvider.CodeDb ?? throw new ArgumentNullException(nameof(dbProvider.CodeDb));
        IDb metadataDb = dbProvider.MetadataDb ?? throw new ArgumentNullException(nameof(dbProvider.MetadataDb));

        _dbMappings = new Dictionary<string, IDb>(StringComparer.InvariantCultureIgnoreCase)
        {
            {DbNames.State, dbProvider.StateDb},
            {DbNames.Storage, dbProvider.StateDb},
            {DbNames.BlockInfos, blockInfosDb},
            {DbNames.Headers, headersDb},
            {DbNames.Metadata, metadataDb},
            {DbNames.Code, codeDb},
        };

        IColumnsDb<ReceiptsColumns> receiptsDb = dbProvider.ReceiptsDb ?? throw new ArgumentNullException(nameof(dbProvider.ReceiptsDb));
        foreach (ReceiptsColumns receiptsDbColumnKey in receiptsDb.ColumnKeys)
        {
            _dbMappings[DbNames.Receipts + receiptsDbColumnKey] = receiptsDb.GetColumnDb(receiptsDbColumnKey);
        }
    }

    public IEnumerable<Block> GetBadBlocks() => _badBlockStore.GetAll();

    public byte[] GetDbValue(string dbName, byte[] key) => _dbMappings[dbName][key];

    public ChainLevelInfo GetLevelInfo(ulong number) => _blockTree.FindLevel(number);

    public ResultWrapper<int> DeleteChainSlice(ulong startNumber, bool force = false)
    {
        ResultWrapper<int>? deletionError = GetDeletionError(startNumber, out _);
        if (deletionError is not null) return deletionError;
        if (!CanMutateChain()) return NotDrained();

        if (!_mutationLock.TryEnter(out BlockTreeMutationLock.Scope mutation, maintenance: true))
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot delete the chain slice from {startNumber}: chain mutation contention or overlapping maintenance; retry the request.");
            return ResultWrapper<int>.Fail("Chain mutation contention or overlapping maintenance; retry the request.", ErrorCodes.ResourceUnavailable);
        }
        using BlockTreeMutationLock.Scope mutationScope = mutation;
        if (!CanMutateChain()) return NotDrained();
        deletionError = GetDeletionError(startNumber, out ulong endNumber);
        if (deletionError is not null) return deletionError;

        bool replacesHead = _blockTree.Head?.Number >= startNumber;
        bool replacesPivot = startNumber <= _blockTree.SyncPivot.BlockNumber &&
                             _blockTree.SyncPivot.BlockNumber <= endNumber;
        Block? target = replacesHead
            ? _blockTree.FindBlock(startNumber - 1, BlockTreeLookupOptions.RequireCanonical)
            : _blockTree.Head;
        if ((replacesHead || replacesPivot) && (target is null || !HasProcessingState(target.Header)))
            return ResultWrapper<int>.Fail("The new head body or state is unavailable for block processing.", ErrorCodes.ResourceUnavailable);

        if (replacesPivot && HasHistoricalProgressAbove(target!.Number))
            return ResultWrapper<int>.Fail("Historical sync progress is above the replacement head; rewind less deeply before deleting chain levels.", ErrorCodes.ResourceUnavailable);

        int deleted = _blockTree.DeleteChainSlice(startNumber, endNumber, force);
        // Completed history remains contiguous from its retained floors to target, below the deleted range.
        // Relocate only after deletion succeeds so a rejected deletion leaves the pivot unchanged.
        if (replacesPivot) _blockTree.SyncPivot = (target!.Number, target.Hash!);
        return ResultWrapper<int>.Success(deleted);

        static ResultWrapper<int> NotDrained() =>
            ResultWrapper<int>.Fail("Pause block processing and wait for it to drain before deleting chain levels.", ErrorCodes.ResourceUnavailable);
    }

    private ResultWrapper<int>? GetDeletionError(ulong startNumber, out ulong endNumber)
    {
        endNumber = _blockTree.BestKnownNumber;
        if (startNumber == 0 || startNumber > endNumber)
            return ResultWrapper<int>.Fail($"startNumber must be positive and cannot exceed the known chain high-water mark ({endNumber}).", ErrorCodes.InvalidParams);
        if (endNumber - startNumber > IBlockTree.MaxDeletionSpan)
            return ResultWrapper<int>.Fail($"The deletion range cannot span more than {IBlockTree.MaxDeletionSpan + 1} chain levels.", ErrorCodes.InvalidParams);

        SyncMode mode = _syncModeSelector.Current;
        if (IsInitialSyncActive(mode))
            return ResultWrapper<int>.Fail("Initial synchronization is active; wait for it to complete before deleting chain levels.", ErrorCodes.ResourceUnavailable);
        if (startNumber <= _blockTree.SyncPivot.BlockNumber)
        {
            if ((mode & SyncMode.FastBlocks) != 0)
                return ResultWrapper<int>.Fail("Ancient backfill is running below the sync pivot; choose a startNumber above it.", ErrorCodes.ResourceUnavailable);
            if (!IsHistoricalSyncFinished())
                return ResultWrapper<int>.Fail("Historical sync is unfinished; wait for it to complete or choose a startNumber above the sync pivot.", ErrorCodes.ResourceUnavailable);
        }

        ulong validatedEnd = endNumber;
        return IsDeleted(_blockTree.LowestInsertedHeader?.Number) ||
               IsDeleted(_syncPointers.LowestInsertedBodyNumber) ||
               IsDeleted(_syncPointers.LowestInsertedReceiptBlockNumber) ||
               IsDeleted(_syncPointers.LowestInsertedBlockAccessListBlockNumber)
            ? ResultWrapper<int>.Fail("Historical sync progress lies in the deletion range; choose a higher startNumber.", ErrorCodes.ResourceUnavailable)
            : null;

        bool IsDeleted(ulong? number) => number >= startNumber && number <= validatedEnd;
    }

    public bool UpdateHeadBlock(Hash256 blockHash) => UpdateHeadBlock(new BlockParameter(blockHash));

    public bool UpdateHeadBlock(BlockParameter blockParameter)
    {
        if (!CanRewindChain()) return false;

        if (!_mutationLock.TryEnter(out BlockTreeMutationLock.Scope mutation, maintenance: true))
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot rewind the head to {blockParameter}: chain mutation contention or overlapping maintenance; retry the request.");
            return false;
        }
        using BlockTreeMutationLock.Scope mutationScope = mutation;
        if (!CanRewindChain()) return false;

        BlockHeader? header = _blockTree.FindHeader(blockParameter);
        if (header is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot rewind the head to {blockParameter}: block is unknown.");
            return false;
        }

        if (!HasProcessingState(header))
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot rewind the head to {blockParameter}: state is unavailable for block processing.");
            return false;
        }

        bool rewindPivot = header.Number < _blockTree.SyncPivot.BlockNumber;
        if (rewindPivot && ((_syncModeSelector.Current & SyncMode.FastBlocks) != 0 ||
                            !IsHistoricalSyncFinished() || HasHistoricalProgressAbove(header.Number)))
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot rewind the head to {blockParameter}: the sync pivot cannot follow it; rewind less deeply.");
            return false;
        }
        if (!_blockTree.TryRewindHead(header.Hash!)) return false;
        // Keep completed sync aligned with the rewound head before state cleanup can yield to the selector.
        if (rewindPivot) _blockTree.SyncPivot = (header.Number, header.Hash!);

        _worldStateManager.DropStateNotReachableFrom(header);
        return true;
    }

    private static bool IsInitialSyncActive(SyncMode mode) => (mode &
        (SyncMode.BeaconHeaders | SyncMode.StateNodes | SyncMode.FastSync | SyncMode.UpdatingPivot | SyncMode.DbLoad)) != 0;

    private bool IsHistoricalSyncFinished() =>
        _syncProgressResolver.IsFastBlocksHeadersFinished() &&
        _syncProgressResolver.IsFastBlocksBodiesFinished() &&
        _syncProgressResolver.IsFastBlocksReceiptsFinished() &&
        _syncProgressResolver.IsFastBlockAccessListsFinished();

    private bool HasHistoricalProgressAbove(ulong number) =>
        _blockTree.LowestInsertedHeader?.Number > number ||
        _syncPointers.LowestInsertedBodyNumber > number ||
        _syncPointers.LowestInsertedReceiptBlockNumber > number ||
        _syncPointers.LowestInsertedBlockAccessListBlockNumber > number;

    private bool CanRewindChain()
    {
        if (!CanMutateChain()) return false;
        if (!IsInitialSyncActive(_syncModeSelector.Current)) return true;
        if (_logger.IsWarn) _logger.Warn("Cannot rewind the head while initial synchronization is active; wait for synchronization to complete.");
        return false;
    }

    private bool CanMutateChain()
    {
        if (_pauseControl.IsPaused && _processingQueue.IsEmpty && !_blockTree.IsProcessingBlock) return true;
        if (_logger.IsWarn) _logger.Warn("Cannot mutate the chain: pause block processing and wait for it to drain.");
        return false;
    }

    private bool HasProcessingState(BlockHeader header) =>
        // The scope provider rejects read-only flat history; the reader enforces trie pruning retention.
        _worldStateManager.GlobalWorldState.HasRoot(header) && _worldStateManager.GlobalStateReader.HasStateForBlock(header);

    public Task<bool> MigrateReceipts(ulong from, ulong to) => _receiptsMigration.Run(from, to);

    public void InsertReceipts(BlockParameter blockParameter, TxReceipt[] txReceipts)
    {
        SearchResult<Block> searchResult = _blockTree.SearchForBlock(blockParameter);
        if (searchResult.IsError)
        {
            throw new InvalidDataException(searchResult.Error);
        }

        Block block = searchResult.Object;
        Hash256 root = ReceiptsRootCalculator.Instance.GetReceiptsRoot(txReceipts, _specProvider.GetSpec(block.Header), block.ReceiptsRoot);
        if (block.ReceiptsRoot != root)
        {
            throw new InvalidDataException("Receipts root mismatch");
        }

        _receiptStorage.Insert(block, txReceipts);
    }

    public GethLikeTxTrace? GetTransactionTrace(Hash256 transactionHash, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        _tracer.Trace(transactionHash, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken, writer, pipeWriter);
    public TxReceipt[]? GetReceiptsForBlock(BlockParameter blockParam)
    {
        SearchResult<Block> searchResult = _blockTree.SearchForBlock(blockParam);
        if (searchResult.IsError)
        {
            throw new InvalidDataException(searchResult.Error);
        }

        Block block = searchResult.Object;
        return _receiptFinder.Get(block);
    }

    public Transaction? GetTransactionFromHash(Hash256 txHash)
    {
        Hash256 blockHash = _receiptStorage.FindBlockHash(txHash);
        if (blockHash is null)
            return null;
        SearchResult<Block> searchResult = _blockTree.SearchForBlock(new BlockParameter(blockHash));
        if (searchResult.IsError)
        {
            throw new InvalidDataException(searchResult.Error);
        }
        Block block = searchResult.Object;
        TxReceipt txReceipt = _receiptFinder.Get(block).ForTransaction(txHash);
        return block?.Transactions[txReceipt.Index];
    }

    [Obsolete("Use the Hash256 overload: a block number resolves only the canonical block at that height.")]
    public GethLikeTxTrace? GetTransactionTrace(ulong blockNumber, int index, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        _tracer.Trace(blockNumber, index, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken, writer, pipeWriter);

    public GethLikeTxTrace? GetTransactionTrace(Hash256 blockHash, int index, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        _tracer.Trace(blockHash, index, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken, writer, pipeWriter);

    public GethLikeTxTrace? GetTransactionTrace(Rlp blockRlp, Hash256 transactionHash, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        _tracer.Trace(blockRlp, transactionHash, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken, writer, pipeWriter);

    public GethLikeTxTrace? GetTransactionTrace(Block block, Hash256 txHash, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        _tracer.Trace(block, txHash, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken, writer, pipeWriter);

    public GethLikeTxTrace? GetTransactionTrace(Transaction transaction, BlockParameter blockParameter, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        _tracer.Trace(blockParameter, transaction, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken, writer, pipeWriter);

    public IReadOnlyCollection<GethLikeTxTrace> GetBlockTrace(BlockParameter blockParameter, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        _tracer.TraceBlock(blockParameter, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken, writer, pipeWriter);

    public IReadOnlyCollection<GethLikeTxTrace> GetBlockTrace(Rlp blockRlp, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        _tracer.TraceBlock(blockRlp, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken, writer, pipeWriter);

    public IReadOnlyCollection<GethLikeTxTrace> GetBlockTrace(Block block, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null, Utf8JsonWriter? writer = null, PipeWriter? pipeWriter = null) =>
        _tracer.TraceBlock(block, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken, writer, pipeWriter);

    public IReadOnlyCollection<Hash256> GetBlockIntermediateRoots(Hash256 blockHash, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null) =>
        _tracer.TraceBlockIntermediateRoots(blockHash, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);

    public byte[]? GetBlockRlp(BlockParameter parameter)
    {
        if (parameter.BlockNumber is ulong number)
        {
            Hash256? hash = _blockTree.FindHash(number);
            if (hash is null) return null;
            return _blockStore.GetRlp(number, hash);
        }
        else
        {
            BlockHeader? header = _blockTree.FindHeader(parameter);
            if (header is null) return null;
            return _blockStore.GetRlp(header.Number, header.GetOrCalculateHash());
        }
    }

    public Block? GetBlock(BlockParameter param)
        => _blockTree.FindBlock(param);

    public object GetConfigValue(string category, string name) => _configProvider.GetRawValue(category, name);

    public SyncReportSummary GetCurrentSyncStage() => new()
    {
        CurrentStage = _syncModeSelector.Current.ToFlagsString()
    };

    public bool HaveNotSyncedHeadersYet() => _syncModeSelector.Current.HaveNotSyncedHeadersYet();

    public IEnumerable<string> TraceBlockToFile(
        Hash256 blockHash,
        CancellationToken cancellationToken,
        GethTraceOptions? gethTraceOptions = null) =>
        _tracer.TraceBlockToFile(blockHash, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);

    public IEnumerable<string> TraceBadBlockToFile(
        Hash256 blockHash,
        CancellationToken cancellationToken,
        GethTraceOptions? gethTraceOptions = null) =>
        _tracer.TraceBadBlockToFile(blockHash, gethTraceOptions ?? GethTraceOptions.Default, cancellationToken);

    public Hash256? GetTransactionBlockHash(Hash256 transactionHash) => _receiptStorage.FindBlockHash(transactionHash);

    public IEnumerable<IEnumerable<GethLikeTxTrace>> GetBundleTraces(TransactionBundle[] bundles, BlockParameter blockParameter, ulong? gasCap, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions = null)
    {
        BlockHeader? header = _blockTree.FindHeader(blockParameter);
        IReleaseSpec? spec = header is null ? null : _specProvider.GetSpec(header);
        foreach (TransactionBundle bundle in bundles)
        {
            yield return GetBundleTrace(bundle, blockParameter, gasCap, spec, cancellationToken, gethTraceOptions);
        }
    }

    private IEnumerable<GethLikeTxTrace> GetBundleTrace(TransactionBundle bundle, BlockParameter blockParameter, ulong? gasCap, IReleaseSpec? spec, CancellationToken cancellationToken, GethTraceOptions? gethTraceOptions)
    {
        foreach (TransactionForRpc txForRpc in bundle.Transactions)
        {
            GethLikeTxTrace? trace;
            Result<Transaction> txResult = txForRpc.ToTransaction(validateUserInput: true, gasCap: gasCap, spec: spec);
            if (txResult.IsError)
            {
                trace = CreateFailTrace(txForRpc.Gas);
            }
            else
            {
                Transaction tx = txResult.Data;

                try
                {
                    trace = _tracer.Trace(
                        blockParameter,
                        tx,
                        gethTraceOptions ?? GethTraceOptions.Default,
                        cancellationToken);
                }
                catch (Exception)
                {
                    trace = CreateFailTrace(tx.GasLimit);
                }
            }

            if (trace is not null)
            {
                yield return trace;
            }
        }

        static GethLikeTxTrace? CreateFailTrace(ulong? gasLimit) => new() { Failed = true, Gas = gasLimit ?? 0UL, ReturnValue = [] };
    }
}
