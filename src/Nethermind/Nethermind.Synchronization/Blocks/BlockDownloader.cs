// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Stats.SyncLimits;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Reporting;
using NonBlocking;

namespace Nethermind.Synchronization.Blocks
{
    public class BlockDownloader : IForwardSyncController
    {
        private static readonly IPeerAllocationStrategy EstimatedAllocationStrategy =
            BlocksSyncPeerAllocationStrategyFactory.AllocationStrategy;

        private static readonly IRlpDecoder<TxReceipt> _receiptEncoder = Rlp.GetDecoder<TxReceipt>() ?? throw new InvalidOperationException();

        private readonly IBlockTree _blockTree;
        private readonly IBlockValidator _blockValidator;
        private readonly ISyncReport _syncReport;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private readonly ISpecProvider _specProvider;
        private readonly IBetterPeerStrategy _betterPeerStrategy;
        private readonly IFullStateFinder _fullStateFinder;
        private readonly IForwardHeaderProvider _forwardHeaderProvider;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly IBlockProcessingQueue _processingQueue;
        private readonly ILogger _logger;

        // Estimated maximum tx in buffer used to estimate memory limit. Each tx is on average about 1KB.
        private readonly int _maxTxInBuffer;
        private readonly int _maxTxInInProcessingQueue;
        private const int MinEstimateTxPerBlock = 10;

        // Header lookup need to be limited, because `IForwardHeaderProvider.GetBlockHeaders` can be slow.
        private const int MaxHeaderLookup = 4 * 1024;

        // This var is updated as blocks get downloaded.
        private int _estimateTxPerBlock = 100;

        // On the off chance that something goes wrong somewhere, request completely hang for example. Retry
        // the request.
        private static readonly TimeSpan RequestHardTimeout = TimeSpan.FromSeconds(30);

        // The forward lookup size determine the buffer size of concurrent download. Estimated from the _maxTxInBuffer
        // and _estimateTxPerBlock. It is capped because of memory limit and to reduce workload by `_forwardHeaderProvider`.
        private int HeaderLookupSize => Math.Min(_maxTxInBuffer / _estimateTxPerBlock, MaxHeaderLookup);

        private readonly ConcurrentDictionary<Hash256, BlockEntry> _downloadRequests = new();
        public int DownloadRequestBufferSize => _downloadRequests.Count;
        private SemaphoreSlim _requestLock = new(1);

        public BlockDownloader(
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            ISyncReport syncReport,
            IReceiptStorage receiptStorage,
            ISpecProvider specProvider,
            IBetterPeerStrategy betterPeerStrategy,
            IFullStateFinder fullStateFinder,
            IForwardHeaderProvider forwardHeaderProvider,
            ISyncPeerPool syncPeerPool,
            IReceiptsRecovery receiptsRecovery,
            IBlockProcessingQueue processingQueue,
            ISyncConfig syncConfig,
            ILogManager logManager)
        {
            _blockTree = blockTree;
            _blockValidator = blockValidator;
            _syncReport = syncReport;
            _receiptStorage = receiptStorage;
            _specProvider = specProvider;
            _betterPeerStrategy = betterPeerStrategy;
            _fullStateFinder = fullStateFinder;
            _forwardHeaderProvider = forwardHeaderProvider;
            _syncPeerPool = syncPeerPool;
            _logger = logManager.GetClassLogger<BlockDownloader>();
            // Assume that each tx cost about 1kb.
            _maxTxInBuffer = (int)(syncConfig.ForwardSyncDownloadBufferMemoryBudget / 1000);
            _maxTxInInProcessingQueue = (int)(syncConfig.ForwardSyncBlockProcessingQueueMemoryBudget / 1000);
            _receiptsRecovery = receiptsRecovery;
            _processingQueue = processingQueue;
            _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
        }

        private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            if (e.Block is null)
            {
                if (_logger.IsError) _logger.Error("Received a new block head which is null.");
                return;
            }

            _syncReport.FullSyncBlocksDownloaded.TargetValue = Math.Max(_syncReport.FullSyncBlocksDownloaded.TargetValue, e.Block.Number);
            _syncReport.FullSyncBlocksDownloaded.Update(_blockTree.BestSuggestedHeader?.Number ?? 0UL);
        }

        public async Task<BlocksRequest?> PrepareRequest(DownloaderOptions options, ulong fastSyncLag, CancellationToken cancellation)
        {
            await _requestLock.WaitAsync(cancellation);
            try
            {
                return await DoPrepareRequest(options, fastSyncLag, cancellation);
            }
            catch (Exception ex)
            {
                _logger.DebugError($"Unhandled exception in {nameof(BlockDownloader)}: {ex}");
                return null;
            }
            finally
            {
                _requestLock.Release();
            }
        }

        private async Task<BlocksRequest?> DoPrepareRequest(DownloaderOptions options, ulong fastSyncLag, CancellationToken cancellation)
        {
            bool originalDownloadReceiptOpts = (options & DownloaderOptions.WithReceipts) == DownloaderOptions.WithReceipts;
            bool originalShouldProcess = (options & DownloaderOptions.Process) == DownloaderOptions.Process;

            int blocksSynced = 0;
            ulong bestProcessedBlock = 0;
            ulong? previousStartingHeaderNumber = null;

            while (true)
            {
                if (_processingQueue.Count > _maxTxInInProcessingQueue / _estimateTxPerBlock)
                {
                    if (_logger.IsTrace) _logger.Trace("Processing queue full");
                    return null;
                }

                using IOwnedReadOnlyList<BlockHeader?>? headers = await _forwardHeaderProvider.GetBlockHeaders(fastSyncLag, (ulong)HeaderLookupSize + 1, cancellation);
                if (cancellation.IsCancellationRequested) return null; // check before every heavy operation
                bool shouldProcess;
                bool downloadReceipts;
                {
                    ReadOnlySpan<BlockHeader?> headersSpan = headers is null ? [] : headers.AsSpan();
                    if (headersSpan.Length <= 1) return null;

                    if (_logger.IsTrace) _logger.Trace($"Prepared request from block {headersSpan[0].Number} to {headersSpan[^1].Number}");

                    if (previousStartingHeaderNumber == headersSpan[0].Number)
                    {
                        // When the block is suggested right between a `NewPayload` and `ForkChoiceUpdatedHandler` the block is not added because it was added already
                        // by NP, but it still a beacon block because `FCU` has not happened yet. Causing this situation.
                        if (_logger.IsDebug) _logger.Debug($"Forward header starting block number did not changed from {previousStartingHeaderNumber}.");
                        return null;
                    }

                    for (int i = 1; i < headersSpan.Length; i++)
                    {
                        if (headersSpan[i].Number - 1 != headersSpan[i - 1].Number)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Non consecutive header sequence from forward header provider. {headersSpan[i].Number} vs {headersSpan[i - 1].Number + 1}");
                            return null;
                        }

                        if (headersSpan[i].ParentHash != headersSpan[i - 1].Hash)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Unexpected parent hash from forward header provider {headersSpan[i].ParentHash} vs {headersSpan[i - 1].Hash}");
                            return null;
                        }
                    }

                    previousStartingHeaderNumber = headersSpan[0].Number;

                    (shouldProcess, downloadReceipts) = ReceiptEdgeCase(bestProcessedBlock, headersSpan[1].Number, originalShouldProcess, originalDownloadReceiptOpts);
                }

                using ArrayPoolList<BlockEntry> satisfiedEntry = AssembleSatisfiedEntries(headers!, shouldProcess, downloadReceipts);

                if (satisfiedEntry.Count == 0)
                {
                    if (_logger.IsTrace) _logger.Trace($"No entries satisfied");
                }
                else
                {
                    if (_logger.IsDebug) _logger.Debug($"Processing {satisfiedEntry.Count} entries from {satisfiedEntry[0]?.Header.Number ?? ulong.MaxValue} to {satisfiedEntry[^1]?.Header.Number ?? ulong.MaxValue}");
                }

                for (int blockIndex = 0; blockIndex < satisfiedEntry.Count; blockIndex++)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
                        break;
                    }

                    BlockEntry entry = satisfiedEntry[blockIndex];
                    Block currentBlock = entry.Block!;

                    if (!_blockValidator.ValidateSuggestedBlock(currentBlock, entry.ParentHeader, out string? errorMessage))
                    {
                        PeerInfo peer = entry.PeerInfo;
                        if (_logger.IsDebug) _logger.Debug($"Invalid downloaded block from {peer}, {errorMessage}");

                        if (peer is not null) _syncPeerPool.ReportBreachOfProtocol(peer, DisconnectReason.ForwardSyncFailed, $"invalid block received: {errorMessage}. Block: {currentBlock.Header.ToString(BlockHeader.Format.Short)}");
                        entry.RetryBlockRequest();

                        // At this point, the chain is somehow invalid. `IForwardHeaderProvider` returned a chain whose block
                        // is interpreted as invalid. `IForwardHeaderProvider` need to provide a different chain later on.
                        return null;
                    }

                    GC.KeepAlive(entry.ParentHeader); // ParentHeader is used with `Header.MaybeParent` to ensure reference is kept.

                    if (SuggestBlock(entry.PeerInfo, entry.Block, blockIndex == 0, shouldProcess, downloadReceipts, entry.Receipts))
                    {
                        if (shouldProcess)
                        {
                            bestProcessedBlock = currentBlock.Number;
                        }

                        blocksSynced++;
                    }
                }

                // Should only happen because of a lot of reorg
                // or if the `HeaderLookupSize` become smaller significantly.
                if (_downloadRequests.Count > headers.Count * 2)
                {
                    PruneRequestMap(headers!);
                }

                if (blocksSynced > 0)
                {
                    _syncReport.FullSyncBlocksDownloaded.Update(_blockTree.BestSuggestedHeader?.Number ?? 0UL);
                }

                _syncReport.FullSyncBlocksDownloaded.CurrentQueued = _downloadRequests.Count;

                if (satisfiedEntry.Count == 0) // Nothing left to process
                {
                    return await AssembleRequest(headers!, shouldProcess, downloadReceipts, cancellation);
                }
            }
        }

        public void PruneDownloadBuffer() => _downloadRequests.Clear();

        private void PruneRequestMap(IOwnedReadOnlyList<BlockHeader> currentHeaders)
        {
            ReadOnlySpan<BlockHeader> currentHeadersSpan = currentHeaders.AsSpan();
            HashSet<Hash256> currentHeaderHashes = new(currentHeadersSpan.Length);
            foreach (BlockHeader header in currentHeadersSpan)
            {
                currentHeaderHashes.Add(header.Hash);
            }

            foreach (KeyValuePair<Hash256, BlockEntry> kv in _downloadRequests)
            {
                if (!currentHeaderHashes.Contains(kv.Key))
                {
                    _downloadRequests.Remove(kv.Key, out _);
                }
            }
        }

        private async Task<BlocksRequest?> AssembleRequest(IOwnedReadOnlyList<BlockHeader> headers, bool shouldProcess, bool shouldDownloadReceipt, CancellationToken cancellation)
        {
            BlocksRequestContentType? requestContentType = null;

            ArrayPoolList<BlockHeader> receiptsToDownload = new(headers.Count);
            ArrayPoolList<BlockHeader> bodiesToDownload = new(headers.Count);
            ArrayPoolList<BlockHeader> blockAccessListsToDownload = new(headers.Count);

            int bodiesRequestSize =
                (await _syncPeerPool.EstimateRequestLimit(RequestType.Bodies, EstimatedAllocationStrategy, AllocationContexts.Blocks, cancellation))
                ?? GethSyncLimits.MaxBodyFetch;
            int? blockAccessListsRequestSize = null;
            int receiptsRequestSize =
                (await _syncPeerPool.EstimateRequestLimit(RequestType.Receipts, EstimatedAllocationStrategy, AllocationContexts.Blocks, cancellation))
                ?? GethSyncLimits.MaxReceiptFetch;

            int headersCount = headers.Count;
            BlockHeader parentHeader = headers[0];
            for (int i = 1; i < headersCount; i++)
            {
                BlockHeader blockHeader = headers[i];
                if (parentHeader.Hash != blockHeader.ParentHash)
                {
                    // Precaution for weird consensus
                    throw new InvalidOperationException($"{nameof(IForwardHeaderProvider)} return a disconnected chain");
                }

                BlockEntry? entry;
                while (!_downloadRequests.TryGetValue(blockHeader.Hash!, out entry))
                {
                    _downloadRequests.TryAdd(
                        blockHeader.Hash,
                        new BlockEntry(
                            parentHeader,
                            blockHeader,
                            Block: null,
                            Receipts: null,
                            PeerInfo: null,
                            EncodedAccessList: null));
                }
                parentHeader = blockHeader;

                if ((requestContentType is null or BlocksRequestContentType.Bodies) && entry.NeedBodyDownload)
                {
                    entry.MarkBlockRequestSent();
                    bodiesToDownload.Add(blockHeader);
                    requestContentType = BlocksRequestContentType.Bodies;
                }

                if (!shouldProcess && (requestContentType is null or BlocksRequestContentType.BlockAccessLists) && entry.NeedAccessListDownload)
                {
                    blockAccessListsRequestSize ??=
                        (await _syncPeerPool.EstimateRequestLimit(RequestType.BlockAccessLists, EstimatedAllocationStrategy, AllocationContexts.BlockAccessLists, cancellation))
                        ?? GethSyncLimits.MaxBodyFetch;

                    if (blockAccessListsToDownload.Count < blockAccessListsRequestSize.Value)
                    {
                        entry.MarkAccessListRequestSent();
                        blockAccessListsToDownload.Add(blockHeader);
                        requestContentType = BlocksRequestContentType.BlockAccessLists;
                    }
                }

                if (
                    shouldDownloadReceipt &&
                    (requestContentType is null or BlocksRequestContentType.Receipts) &&
                    entry.NeedReceiptDownload)
                {
                    entry.MarkReceiptRequestSent();
                    receiptsToDownload.Add(blockHeader);
                    requestContentType = BlocksRequestContentType.Receipts;
                }

                if (bodiesToDownload.Count >= bodiesRequestSize)
                {
                    break;
                }
                if (blockAccessListsRequestSize is not null && blockAccessListsToDownload.Count >= blockAccessListsRequestSize.Value)
                {
                    break;
                }
                if (receiptsToDownload.Count >= receiptsRequestSize)
                {
                    break;
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Assembled request of {bodiesToDownload.Count} bodies, {blockAccessListsToDownload.Count} access lists and {receiptsToDownload.Count} receipts.");

            if (receiptsToDownload.Count + bodiesToDownload.Count + blockAccessListsToDownload.Count == 0)
            {
                bodiesToDownload.Dispose();
                blockAccessListsToDownload.Dispose();
                receiptsToDownload.Dispose();
                return null;
            }

            return new BlocksRequest()
            {
                BodiesRequests = bodiesToDownload,
                BlockAccessListsRequests = blockAccessListsToDownload,
                ReceiptsRequests = receiptsToDownload,
            };
        }

        private ArrayPoolList<BlockEntry> AssembleSatisfiedEntries(IOwnedReadOnlyList<BlockHeader?> headers, bool shouldProcess, bool shouldDownloadReceipt)
        {
            ArrayPoolList<BlockEntry>? satisfiedEntry = null;
            try
            {
                satisfiedEntry = new ArrayPoolList<BlockEntry>(headers.Count);
                ReadOnlySpan<BlockHeader?> headersSpan = headers.AsSpan();
                for (int i = 1; i < headersSpan.Length; i++)
                {
                    BlockHeader? blockHeader = headersSpan[i];
                    if (blockHeader is null) break;
                    if (!_downloadRequests.TryGetValue(blockHeader.Hash, out BlockEntry blockEntry)) break;
                    if (blockEntry.Block is null) break;
                    if (!shouldProcess && !blockEntry.HasAccessList) break;
                    if (shouldDownloadReceipt && !blockEntry.HasReceipt) break;

                    satisfiedEntry.Add(blockEntry);
                    _downloadRequests.Remove(blockHeader.Hash, out _);
                }

                return satisfiedEntry;
            }
            catch
            {
                satisfiedEntry?.Dispose();
                throw;
            }
        }

        public SyncResponseHandlingResult HandleResponse(BlocksRequest response, PeerInfo? peer)
        {
            using BlocksRequest _ = response;
            BlockBody[]? bodies = response.OwnedBodies?.Bodies;
            response.OwnedBodies?.Disown();
            IOwnedReadOnlyList<byte[]?>? blockAccessLists = response.BlockAccessLists;
            bool unsupportedBlockAccessListsPeer = response.BlockAccessListsRequests.Count > 0 &&
                                                   peer is not null &&
                                                   !peer.SyncPeer.SupportsBlockAccessLists();

            SyncResponseHandlingResult result = SyncResponseHandlingResult.OK;
            using ArrayPoolListRef<Block> blocks = new(response.BodiesRequests?.Count ?? 0);
            int bodiesCount = 0;
            int blockAccessListsCount = 0;
            int receiptsCount = 0;

            for (int i = 0; i < response.BodiesRequests.Count; i++)
            {
                BlockHeader header = response.BodiesRequests[i];
                if (!_downloadRequests.TryGetValue(header.Hash, out BlockEntry entry))
                {
                    continue;
                }

                if ((bodies?.Length ?? 0) <= i)
                {
                    entry.RetryBlockRequest();
                    continue;
                }

                BlockBody? body = bodies[i];
                if (body is null)
                {
                    entry.RetryBlockRequest();
                    continue;
                }

                if (!_blockValidator.ValidateBodyAgainstHeader(entry.Header, body, out string errorMessage))
                {
                    if (_logger.IsDebug) _logger.Debug($"Invalid downloaded block from {peer}, {errorMessage}");

                    if (peer is not null) _syncPeerPool.ReportBreachOfProtocol(peer, DisconnectReason.ForwardSyncFailed, $"invalid block received: {errorMessage}. Block: {entry.Header.ToString(BlockHeader.Format.Short)}");
                    result = SyncResponseHandlingResult.LesserQuality;
                    entry.RetryBlockRequest();
                    continue;
                }

                Block block = new(entry.Header, body)
                {
                    EncodedBlockAccessList = entry.EncodedAccessList
                };

                if (_logger.IsTrace) _logger.Trace($"Adding block to requests map {entry.Header.Number}");
                entry.Block = block;
                entry.PeerInfo = peer;
                blocks.Add(block);
                bodiesCount++;
            }

            if (result == SyncResponseHandlingResult.OK)
            {
                if (bodiesCount > 0)
                {
                    long txCount = 0;
                    foreach (Block block in blocks)
                    {
                        txCount += block.Transactions?.Length ?? 0;
                    }
                    _estimateTxPerBlock = (int)Math.Max(txCount / blocks.Count, MinEstimateTxPerBlock);
                }
            }

            for (int i = 0; i < response.ReceiptsRequests.Count; i++)
            {
                BlockHeader header = response.ReceiptsRequests[i];
                if (!_downloadRequests.TryGetValue(header.Hash, out BlockEntry entry))
                {
                    continue;
                }

                if ((response.Receipts?.Count ?? 0) <= i)
                {
                    entry.RetryReceiptRequest();
                    continue;
                }

                TxReceipt[]? receipts = response.Receipts[i];
                if (receipts is null)
                {
                    entry.RetryReceiptRequest();
                    continue;
                }

                Block block = entry.Block!;

                if (block is null)
                {
                    // Could happen if the buffer is reduced and then reenlarged.
                    entry.RetryBlockRequest();
                    entry.RetryReceiptRequest();
                    continue;
                }

                if (_receiptsRecovery.TryRecover(block, receipts, false) == ReceiptsRecoveryResult.Fail)
                {
                    if (_logger.IsDebug) _logger.Debug($"Recovery failure from {peer} for block {header.ToString(BlockHeader.Format.Short)}");
                    if (peer is not null) _syncPeerPool.ReportBreachOfProtocol(peer, DisconnectReason.ForwardSyncFailed, "receipt recovery failed");
                    result = SyncResponseHandlingResult.LesserQuality;
                    entry.RetryReceiptRequest();
                    continue;
                }
                if (!ValidateReceiptsRoot(block, receipts))
                {
                    if (_logger.IsDebug) _logger.Debug($"Invalid receipt root from {peer} for block {header.ToString(BlockHeader.Format.Short)}");

                    if (peer is not null) _syncPeerPool.ReportBreachOfProtocol(peer, DisconnectReason.ForwardSyncFailed, "invalid receipt root");
                    result = SyncResponseHandlingResult.LesserQuality;
                    entry.RetryReceiptRequest();
                    continue;
                }

                if (_logger.IsTrace) _logger.Trace($"Adding receipts to requests map {entry.Header.Number}");
                entry.Receipts = receipts;
                entry.PeerInfo = peer;
                receiptsCount++;
            }

            for (int i = 0; i < response.BlockAccessListsRequests.Count; i++)
            {
                BlockHeader header = response.BlockAccessListsRequests[i];
                if (!_downloadRequests.TryGetValue(header.Hash, out BlockEntry entry))
                {
                    continue;
                }

                if (TryHandleBlockAccessListResponse(header, entry, blockAccessLists, i, unsupportedBlockAccessListsPeer, peer, ref result))
                {
                    blockAccessListsCount++;
                }
            }

            if (result == SyncResponseHandlingResult.OK)
            {
                // Request and body does not have the same size so this heuristic is wrong.
                if (bodiesCount + blockAccessListsCount + receiptsCount == 0)
                {
                    // Trigger sleep
                    result = SyncResponseHandlingResult.LesserQuality;
                }
            }

            HandleSyncRequestResult(response.DownloadTask, peer);

            return result;
        }

        private bool TryHandleBlockAccessListResponse(
            BlockHeader header,
            BlockEntry entry,
            IOwnedReadOnlyList<byte[]?>? blockAccessLists,
            int index,
            bool unsupportedBlockAccessListsPeer,
            PeerInfo? peer,
            ref SyncResponseHandlingResult result)
        {
            if ((blockAccessLists?.Count ?? 0) <= index)
            {
                if (unsupportedBlockAccessListsPeer)
                {
                    result = SyncResponseHandlingResult.LesserQuality;
                }

                entry.RetryAccessListRequest();
                return false;
            }

            byte[]? encodedAccessList = blockAccessLists[index];
            if (encodedAccessList is null)
            {
                entry.RetryAccessListRequest();
                return false;
            }

            if (!BlockAccessListHashValidator.Validate(header, encodedAccessList, out string? errorMessage))
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid block access list from {peer} for block {header.ToString(BlockHeader.Format.Short)}, {errorMessage}");

                if (peer is not null) _syncPeerPool.ReportBreachOfProtocol(peer, DisconnectReason.ForwardSyncFailed, $"invalid block access list received: {errorMessage}. Block: {header.ToString(BlockHeader.Format.Short)}");
                result = SyncResponseHandlingResult.LesserQuality;
                entry.RetryAccessListRequest();
                return false;
            }

            entry.EncodedAccessList = encodedAccessList;
            if (entry.Block is not null)
            {
                entry.Block.EncodedBlockAccessList = encodedAccessList;
            }

            entry.PeerInfo = peer;
            return true;
        }

        private enum BlocksRequestContentType
        {
            Bodies,
            BlockAccessLists,
            Receipts
        }

        private bool ValidateReceiptsRoot(Block block, TxReceipt[] blockReceipts)
        {
            Hash256 receiptsRoot = ReceiptTrie.CalculateRoot(_specProvider.GetSpec(block.Header), blockReceipts, _receiptEncoder);
            return receiptsRoot == block.ReceiptsRoot;
        }

        protected virtual BlockTreeSuggestOptions GetSuggestOption(bool shouldProcess, Block currentBlock) => shouldProcess ? BlockTreeSuggestOptions.ShouldProcess : BlockTreeSuggestOptions.None;

        private bool SuggestBlock(
            PeerInfo bestPeer,
            Block currentBlock,
            bool isFirstInBatch,
            bool shouldProcess,
            bool downloadReceipts,
            TxReceipt[]? receipts)
        {
            BlockTreeSuggestOptions suggestOptions = GetSuggestOption(shouldProcess, currentBlock);
            if (_logger.IsDebug) _logger.Debug($"Suggesting block {currentBlock.Header.ToString(BlockHeader.Format.Short)} with option {suggestOptions}");
            AddBlockResult addResult = _blockTree.SuggestBlock(currentBlock, suggestOptions);
            bool handled = false;
            if (HandleAddResult(bestPeer, currentBlock.Header, isFirstInBatch, addResult))
            {
                if (downloadReceipts)
                {
                    if (receipts is not null)
                    {
                        _receiptStorage.Insert(currentBlock, receipts);
                    }
                    else
                    {
                        // this shouldn't now happen with new validation above, still lets keep this check
                        if (currentBlock.Header.HasTransactions)
                        {
                            if (_logger.IsError) _logger.Error($"{currentBlock} is missing receipts");
                        }
                    }
                }
                handled = true;
            }

            if (!shouldProcess)
            {
                _blockTree.TryUpdateMainChain(currentBlock.Header, wereProcessed: false, preloadedBlocks: [currentBlock]);
            }

            _forwardHeaderProvider.OnSuggestBlock(suggestOptions, currentBlock, addResult);

            return handled;
        }

        private (bool shouldProcess, bool shouldDownloadReceipt) ReceiptEdgeCase(
            ulong bestProcessedBlock,
            ulong firstBlockNumber,
            bool shouldProcess,
            bool shouldDownloadReceipt)
        {
            if (shouldProcess && !shouldDownloadReceipt)
            {
                ulong firstBlock = firstBlockNumber;
                // TODO: Double check this condition
                // An edge case where we already have the state but are still downloading preceding blocks.
                // We cannot process such blocks, but we are still requested to process them via blocksRequest.Options.
                // Therefore, we detect this situation and switch from processing to receipts downloading.
                bool headIsGenesis = _blockTree.Head?.IsGenesis ?? false;
                bool toBeProcessedHasNoProcessedParent = firstBlock > (bestProcessedBlock + 1);
                bool isFastSyncTransition = headIsGenesis && toBeProcessedHasNoProcessedParent;
                if (isFastSyncTransition)
                {
                    ulong bestFullState = _fullStateFinder.FindBestFullState();
                    shouldProcess = firstBlock > bestFullState && bestFullState != 0;
                    if (!shouldProcess)
                    {
                        if (_logger.IsInfo) _logger.Info($"Turning on receipt download in full sync, currentBlock: {firstBlock}, bestFullState: {bestFullState}, trying to load receipts");
                        shouldDownloadReceipt = true;
                    }
                }
            }

            return (shouldProcess, shouldDownloadReceipt);
        }

        private bool HandleAddResult(PeerInfo peerInfo, BlockHeader block, bool isFirstInBatch, AddBlockResult addResult)
        {
            void UpdatePeerInfo(PeerInfo peer, BlockHeader header)
            {
                if (peer?.SyncPeer is not null && header.Hash is not null && header.TotalDifficulty is not null && _betterPeerStrategy.Compare(header, peer?.SyncPeer) > 0)
                {
                    peer.SyncPeer.TotalDifficulty = header.TotalDifficulty.Value;
                    peer.SyncPeer.HeadNumber = header.Number;
                    peer.SyncPeer.HeadHash = header.Hash;
                }
            }
            switch (addResult)
            {
                // this generally should not happen as there is a consistency check before
                case AddBlockResult.UnknownParent:
                    {
                        if (_logger.IsTrace) _logger.Trace($"Block/header {block.Number} ignored (unknown parent)");
                        string blockId = $"#{block.Number} ({block.Hash}, parent {block.ParentHash})";
                        string message = isFirstInBatch
                            ? $"Peer {peerInfo} sent orphaned blocks/headers inside the batch: {blockId}"
                            : $"Peer {peerInfo} sent an inconsistent batch of blocks/headers: {blockId}";
                        _logger.Error(message);
                        throw new EthSyncException(message);
                    }
                case AddBlockResult.CannotAccept:
                    {
                        string message = $"Block tree rejected block/header from peer {peerInfo}: " +
                                         $"#{block.Number} ({block.Hash}, parent {block.ParentHash})";
                        throw new EthSyncException(message);
                    }
                case AddBlockResult.InvalidBlock:
                    {
                        // Validator-level reason (e.g. InvalidReceiptsRoot, BAL hash mismatch) is logged
                        // separately via InvalidBlockException by the validator/processor; see preceding
                        // 'Rejected invalid block' log lines and the RLP dump at /tmp/block_<hash>.rlp.
                        string message = $"Peer {peerInfo} sent an invalid block/header: " +
                                         $"#{block.Number} ({block.Hash}, parent {block.ParentHash})";
                        throw new EthSyncException(message);
                    }
                case AddBlockResult.Added:
                    UpdatePeerInfo(peerInfo, block);
                    if (_logger.IsTrace) _logger.Trace($"Block/header {block.Number} suggested for processing");
                    return true;
                case AddBlockResult.AlreadyKnown:
                    UpdatePeerInfo(peerInfo, block);
                    if (_logger.IsTrace) _logger.Trace($"Block/header {block.Number} skipped - already known");
                    return false;
                default:
                    throw new NotSupportedException($"Unknown {nameof(AddBlockResult)} {addResult}");
            }
        }

        public event EventHandler<SyncEventArgs>? SyncEvent;

        private void InvokeEvent(SyncEventArgs args) => SyncEvent?.Invoke(this, args);

        private void HandleSyncRequestResult(Task task, PeerInfo? peerInfo)
        {
            switch (task)
            {
                case { IsFaulted: true } t:
                    string reason;
                    if (t.HasTimeoutException())
                    {
                        if (_logger.IsDebug) _logger.Debug($"Block download from {peerInfo} timed out. {t.Exception?.Message}");
                        reason = "timeout";
                    }
                    else if (t.HasCanceledException())
                    {
                        if (_logger.IsDebug) _logger.Debug($"Block download from {peerInfo} was canceled.");
                        reason = "cancel";
                    }
                    else
                    {
                        _logger.DebugError($"Block download from {peerInfo} failed. {t.Exception}");
                        // ReSharper disable once RedundantAssignment
                        reason = $"sync fault";
#if DEBUG
                        reason = $"sync fault| {t.Exception}";
#endif
                    }

                    if (peerInfo is not null && !t.HasTimeoutException() && !t.HasCanceledException()) // fix this for node data sync
                    {
                        peerInfo.SyncPeer.Disconnect(DisconnectReason.ForwardSyncFailed, reason);
                        // redirect sync event from block downloader here (move this one inside)
                        InvokeEvent(new SyncEventArgs(peerInfo.SyncPeer, Synchronization.SyncEvent.Failed));
                    }

                    break;
                case { IsCanceled: true }:
                    if (_logger.IsTrace) _logger.Trace($"Blocks download from {peerInfo} canceled. Removing node from sync peers.");

                    break;
                case { IsCompletedSuccessfully: true } t:
                    if (_logger.IsDebug) _logger.Debug($"Blocks download from {peerInfo} completed.");
                    if (peerInfo is not null) // fix this for node data sync
                    {
                        InvokeEvent(new SyncEventArgs(peerInfo.SyncPeer, Synchronization.SyncEvent.Completed));
                    }

                    break;
            }
        }

        private record BlockEntry(
            BlockHeader ParentHeader, // Needed to keep the `MaybeParent` of the `Header` alive.
            BlockHeader Header,
            Block? Block,
            TxReceipt[]? Receipts,
            PeerInfo? PeerInfo,
            byte[]? EncodedAccessList
        )
        {
            public bool HasAccessList => Header.BlockAccessListHash is null || EncodedAccessList is not null || Block?.BlockAccessList is not null;
            public bool HasReceipt => !Header.HasTransactions || Receipts?.Length > 0;
            private DateTimeOffset _blockRequestDeadline = DateTimeOffset.MinValue;
            public bool NeedBodyDownload => Block is null && _blockRequestDeadline < DateTimeOffset.Now;
            public void MarkBlockRequestSent() => _blockRequestDeadline = DateTimeOffset.UtcNow + RequestHardTimeout;
            public void RetryBlockRequest() => _blockRequestDeadline = DateTimeOffset.MinValue;

            private DateTimeOffset _accessListRequestDeadline = DateTimeOffset.MinValue;
            public bool NeedAccessListDownload =>
                !HasAccessList &&
                _accessListRequestDeadline < DateTimeOffset.Now;

            public void MarkAccessListRequestSent() => _accessListRequestDeadline = DateTimeOffset.UtcNow + RequestHardTimeout;

            public void RetryAccessListRequest() => _accessListRequestDeadline = DateTimeOffset.MinValue;

            private DateTimeOffset _receiptRequestDeadline = DateTimeOffset.MinValue;
            public bool NeedReceiptDownload =>
                Block is not null &&
                !HasReceipt &&
                _receiptRequestDeadline < DateTimeOffset.Now;

            public void MarkReceiptRequestSent() => _receiptRequestDeadline = DateTimeOffset.UtcNow + RequestHardTimeout;
            public void RetryReceiptRequest() => _receiptRequestDeadline = DateTimeOffset.MinValue;

            public PeerInfo? PeerInfo { get; set; } = PeerInfo;
            public Block? Block { get; set; } = Block;
            public TxReceipt[]? Receipts { get; set; } = Receipts;
            public byte[]? EncodedAccessList { get; set; } = EncodedAccessList;
        }
    }
}
