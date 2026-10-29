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
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Core.Extensions;
using Nethermind.History;
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
        private readonly BlockAccessListServingWindow _balServingWindow;
        private readonly ILogger _logger;

        // Estimated maximum tx in buffer used to estimate memory limit. Each tx is on average about 1KB.
        private readonly int _maxTxInBuffer;
        private readonly int _maxTxInInProcessingQueue;
        private const int MinEstimateTxPerBlock = 10;

        // Header lookup need to be limited, because `IForwardHeaderProvider.GetBlockHeaders` can be slow.
        private const int MaxHeaderLookup = 4 * 1024;

        // This var is updated as blocks get downloaded.
        private int _estimateTxPerBlock = 100;

        // Downloaded access lists stay in the buffer until suggestion. Their kilobytes per block count as transactions.
        private int _estimateAccessListKbPerBlock;

        // On the off chance that something goes wrong somewhere, request completely hang for example. Retry
        // the request.
        private static readonly TimeSpan RequestHardTimeout = TimeSpan.FromSeconds(30);

        private const long UnservedAccessListWarningIntervalMs = 60_000;
        private long _nextUnservedAccessListWarningMs;

        // The forward lookup size determine the buffer size of concurrent download. Estimated from the _maxTxInBuffer
        // and _estimateTxPerBlock. It is capped because of memory limit and to reduce workload by `_forwardHeaderProvider`.
        private int HeaderLookupSize => Math.Min(_maxTxInBuffer / (_estimateTxPerBlock + _estimateAccessListKbPerBlock), MaxHeaderLookup);

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
            BlockAccessListServingWindow balServingWindow,
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
            _balServingWindow = balServingWindow;
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
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // A cancelled request is not a downloader failure: the dispatcher owns cancellation
                // handling. A cancellation raised by another token still falls through to the
                // catch-all below, so a single bad response cannot finish the feed.
                throw;
            }
            catch (BlockTreeNotReadyException e)
            {
                if (_logger.IsDebug) _logger.Debug($"Block download deferred: {e.Message}");
                return null;
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

                    (shouldProcess, downloadReceipts) = ReceiptEdgeCase(headersSpan[1].Number, originalShouldProcess, originalDownloadReceiptOpts);
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

                    if (!IsValidDownloadedBlock(currentBlock, entry.ParentHeader, out string? errorMessage))
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

                    if (SuggestBlock(entry.PeerInfo, entry.Block, blockIndex == 0, shouldProcess, ShouldDownloadReceipts(entry.Header, shouldProcess, downloadReceipts), entry.Receipts))
                    {
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

        private bool IsValidDownloadedBlock(Block block, BlockHeader parent, out string? errorMessage)
        {
            if (!HeaderValidator.ValidateHash(block.Header, out Hash256 actualHash))
            {
                errorMessage = BlockErrorMessages.InvalidHeaderHash(block.Header.Hash!, actualHash);
                return false;
            }

            return _blockValidator.ValidateSuggestedBlock(block, parent, out errorMessage, validateHashes: false);
        }

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

        // The estimate waits up to a second for a peer to allocate; with no free eth/71 peer that only delays the batch.
        private bool HasFreeBlockAccessListsPeer()
        {
            foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
            {
                if (peer.CanBeAllocated(AllocationContexts.BlockAccessLists)) return true;
            }

            return false;
        }

        /// <summary>The highest head of the peers that serve block access lists, or <c>null</c> when no such peer is connected.</summary>
        /// <remarks>The access list allocation takes only a peer whose head is at or above the requested blocks.</remarks>
        private ulong? GetBlockAccessListsPeerHead()
        {
            ulong? head = null;
            foreach (PeerInfo peer in _syncPeerPool.InitializedPeers)
            {
                if (peer.SyncPeer.SupportsBlockAccessLists()) head = ulong.Max(head ?? 0, peer.HeadNumber);
            }

            return head;
        }

        private async Task<BlocksRequest?> AssembleRequest(IOwnedReadOnlyList<BlockHeader> headers, bool shouldProcess, bool shouldDownloadReceipt, CancellationToken cancellation)
        {
            BlocksRequestContentType? requestContentType = null;

            ArrayPoolList<BlockHeader> receiptsToDownload = new(headers.Count);
            ArrayPoolList<int> expectedReceiptCounts = new(headers.Count);
            ArrayPoolList<BlockHeader> bodiesToDownload = new(headers.Count);
            ArrayPoolList<BlockHeader> blockAccessListsToDownload = new(headers.Count);

            int bodiesRequestSize =
                (await _syncPeerPool.EstimateRequestLimit(RequestType.Bodies, EstimatedAllocationStrategy, AllocationContexts.Blocks, cancellation))
                ?? GethSyncLimits.MaxBodyFetch;
            int? blockAccessListsRequestSize = null;
            ulong? blockAccessListsPeerHead = null;
            bool blockAccessListsPeerHeadKnown = false;
            bool accessListsInBuffer = false;
            int receiptsRequestSize =
                (await _syncPeerPool.EstimateRequestLimit(RequestType.Receipts, BlocksSyncPeerAllocationStrategyFactory.ReceiptsAllocationStrategy, AllocationContexts.Blocks, cancellation))
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
                            Block: TryGetLocalBlock(blockHeader),
                            Receipts: null,
                            PeerInfo: null,
                            EncodedAccessList: null)
                        { AccessListStored = IsAccessListStored(blockHeader) });
                }
                entry.CanExecuteMissingData = shouldProcess && ShouldDownloadAccessList(blockHeader, true);
                parentHeader = blockHeader;

                // Without an eth/71 peer that has the block every request for an optional list fails, so the block executes
                // without waiting for the retries. This is checked on each request, so a peer that connects later serves the list.
                if (entry.CanExecuteMissingData)
                {
                    if (!blockAccessListsPeerHeadKnown)
                    {
                        blockAccessListsPeerHead = GetBlockAccessListsPeerHead();
                        blockAccessListsPeerHeadKnown = true;
                    }

                    entry.NoAccessListPeer = blockAccessListsPeerHead is not { } peerHead || blockHeader.Number > peerHead;
                }

                if ((requestContentType is null or BlocksRequestContentType.Bodies) && entry.NeedBodyDownload)
                {
                    entry.MarkBlockRequestSent();
                    bodiesToDownload.Add(blockHeader);
                    requestContentType = BlocksRequestContentType.Bodies;
                }

                bool downloadAccessList = ShouldDownloadAccessList(blockHeader, shouldProcess);
                accessListsInBuffer |= entry.EncodedAccessList is not null || (downloadAccessList && !entry.HasAccessList);
                if (downloadAccessList && (requestContentType is null or BlocksRequestContentType.BlockAccessLists) && entry.NeedAccessListDownload)
                {
                    blockAccessListsRequestSize ??=
                        (HasFreeBlockAccessListsPeer()
                            ? await _syncPeerPool.EstimateRequestLimit(RequestType.BlockAccessLists, BlocksSyncPeerAllocationStrategyFactory.BlockAccessListsAllocationStrategy, AllocationContexts.BlockAccessLists, cancellation)
                            : null)
                        ?? GethSyncLimits.MaxBodyFetch;

                    if (blockAccessListsToDownload.Count < blockAccessListsRequestSize.Value)
                    {
                        entry.MarkAccessListRequestSent();
                        blockAccessListsToDownload.Add(blockHeader);
                        requestContentType = BlocksRequestContentType.BlockAccessLists;
                    }
                }

                if (
                    ShouldDownloadReceipts(blockHeader, shouldProcess, shouldDownloadReceipt) &&
                    (requestContentType is null or BlocksRequestContentType.Receipts) &&
                    entry.NeedReceiptDownload)
                {
                    entry.MarkReceiptRequestSent();
                    receiptsToDownload.Add(blockHeader);
                    expectedReceiptCounts.Add(entry.Block!.Transactions.Length);
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

            // When no block in the buffer holds or still needs a list, e.g. all are stored or below the serving window,
            // the last downloaded list size no longer limits the buffer. A break leaves headers unchecked, so it keeps the estimate.
            if (!accessListsInBuffer && bodiesToDownload.Count < bodiesRequestSize && receiptsToDownload.Count < receiptsRequestSize)
            {
                _estimateAccessListKbPerBlock = 0;
            }

            if (_logger.IsTrace) _logger.Trace($"Assembled request of {bodiesToDownload.Count} bodies, {blockAccessListsToDownload.Count} access lists and {receiptsToDownload.Count} receipts.");

            if (receiptsToDownload.Count + bodiesToDownload.Count + blockAccessListsToDownload.Count == 0)
            {
                bodiesToDownload.Dispose();
                blockAccessListsToDownload.Dispose();
                receiptsToDownload.Dispose();
                expectedReceiptCounts.Dispose();
                return null;
            }

            return new BlocksRequest()
            {
                BodiesRequests = bodiesToDownload,
                BlockAccessListsRequests = blockAccessListsToDownload,
                ReceiptsRequests = receiptsToDownload,
                ExpectedReceiptCounts = expectedReceiptCounts,
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
                    if (!_downloadRequests.TryGetValue(blockHeader.Hash, out BlockEntry blockEntry))
                    {
                        if (TryGetLocalBlock(blockHeader) is not { } localBlock) break;
                        blockEntry = _downloadRequests.GetOrAdd(blockHeader.Hash, new BlockEntry(headersSpan[i - 1]!, blockHeader, localBlock, Receipts: null, PeerInfo: null, EncodedAccessList: null)
                        { AccessListStored = IsAccessListStored(blockHeader) });
                    }

                    blockEntry.CanExecuteMissingData = shouldProcess && ShouldDownloadAccessList(blockHeader, true);
                    if (blockEntry.Block is null) break;
                    if (ShouldDownloadAccessList(blockHeader, shouldProcess) && !blockEntry.HasAccessList) break;
                    if (ShouldDownloadReceipts(blockHeader, shouldProcess, shouldDownloadReceipt) && !blockEntry.HasReceipt) break;

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

            // Body replies are sparse: a peer omits the bodies it does not have, so the reply is consumed
            // with a cursor and a body that does not fit its header is retried against the headers behind it.
            int bodyIndex = 0;
            Hash256? rejectedBodyTxRoot = null;
            for (int i = 0; i < response.BodiesRequests.Count; i++)
            {
                BlockHeader header = response.BodiesRequests[i];
                _downloadRequests.TryGetValue(header.Hash, out BlockEntry? entry);

                if ((bodies?.Length ?? 0) <= bodyIndex)
                {
                    entry?.RetryBlockRequest();
                    continue;
                }

                BlockBody? body = bodies![bodyIndex];
                if (body is null)
                {
                    bodyIndex++;
                    rejectedBodyTxRoot = null;
                    entry?.RetryBlockRequest();
                    continue;
                }

                string? bodyError = null;
                bool bodyFitsHeader = (rejectedBodyTxRoot is null || rejectedBodyTxRoot == header.TxRoot)
                                      && _blockValidator.ValidateBodyAgainstHeader(header, body, out bodyError);
                if (!bodyFitsHeader)
                {
                    if (_logger.IsTrace) _logger.Trace($"Body {bodyIndex} from {peer} does not fit {header.ToString(BlockHeader.Format.Short)}: {bodyError ?? "tx root differs"}");
                    rejectedBodyTxRoot ??= TxTrie.CalculateRoot(body.Transactions);
                    entry?.RetryBlockRequest();
                    continue;
                }

                bodyIndex++;
                rejectedBodyTxRoot = null;
                if (entry is null)
                {
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

            if (bodyIndex < (bodies?.Length ?? 0))
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid downloaded block from {peer}, a body matches none of the requested headers");

                if (peer is not null) _syncPeerPool.ReportBreachOfProtocol(peer, DisconnectReason.ForwardSyncFailed, "invalid block received: body matches none of the requested headers");
                result = SyncResponseHandlingResult.LesserQuality;
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

                int receiptsServed = response.Receipts?.Count ?? 0;
                if (receiptsServed <= i)
                {
                    // A non-empty reply cut short by the peer's size limit is not evidence the data is missing.
                    if (receiptsServed == 0) entry.RecordMissingReceipt();
                    entry.RetryReceiptRequest();
                    continue;
                }

                TxReceipt[]? receipts = response.Receipts[i];
                if (receipts is null)
                {
                    entry.RecordMissingReceipt();
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
                entry.ResetMissingReceipt();
                entry.Receipts = receipts;
                entry.PeerInfo = peer;
                receiptsCount++;
            }

            long blockAccessListsBytes = 0;
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
                    blockAccessListsBytes += entry.EncodedAccessList!.Length;
                }
            }

            if (blockAccessListsCount > 0)
            {
                _estimateAccessListKbPerBlock = (int)(blockAccessListsBytes / blockAccessListsCount / 1000);
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
            int served = blockAccessLists?.Count ?? 0;
            if (served <= index)
            {
                if (unsupportedBlockAccessListsPeer)
                {
                    result = SyncResponseHandlingResult.LesserQuality;
                }

                if (served == 0) entry.RecordMissingAccessList();
                entry.RetryAccessListRequest();
                return false;
            }

            byte[]? encodedAccessList = blockAccessLists[index];
            if (encodedAccessList is null)
            {
                entry.RecordMissingAccessList();
                entry.RetryAccessListRequest();
                bool mustServe = _balServingWindow.MustServe(header, peer?.HeadNumber ?? 0);
                // Outside the serving window (e.g. full sync resuming far behind after downtime) a required list may be
                // pruned on every peer, so the answer is not proof of withholding.
                if (mustServe || !entry.CanExecuteMissingData)
                {
                    result = SyncResponseHandlingResult.LesserQuality;
                    WarnUnservedAccessList(header, peer, mustServe, entry.CanExecuteMissingData);
                    // The peer that served this block's body or receipts has the block, so it is withholding the list.
                    // When processing can go ahead without the list, a peer still backfilling lists is not worth dropping.
                    if (mustServe && !entry.CanExecuteMissingData && peer is not null && ReferenceEquals(entry.PeerInfo, peer))
                    {
                        _syncPeerPool.ReportBreachOfProtocol(peer, DisconnectReason.ForwardSyncFailed, $"withheld block access list. Block: {header.ToString(BlockHeader.Format.Short)}");
                    }
                }
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

            entry.ResetMissingAccessList();
            entry.EncodedAccessList = encodedAccessList;
            if (entry.Block is not null)
            {
                entry.Block.EncodedBlockAccessList = encodedAccessList;
            }

            entry.PeerInfo = peer;
            return true;
        }

        /// <summary>Warns, at most once a minute, that a static or trusted peer did not serve a block access list it must serve.</summary>
        /// <remarks>
        /// Only an operator-chosen peer is worth a warning: with it as the sole peer, sync cannot pass a block that needs the list,
        /// and a block that can execute without it executes slower.
        /// </remarks>
        private void WarnUnservedAccessList(BlockHeader header, PeerInfo? peer, bool insideServingWindow, bool canExecuteWithoutList)
        {
            if (!_logger.IsWarn || peer?.SyncPeer.Node is not { } node || !(node.IsStatic || node.IsTrusted)) return;

            long now = Environment.TickCount64;
            long next = Interlocked.Read(ref _nextUnservedAccessListWarningMs);
            if (now < next || Interlocked.CompareExchange(ref _nextUnservedAccessListWarningMs, now + UnservedAccessListWarningIntervalMs, next) != next) return;

            string reason = insideServingWindow ? "withheld" : "withheld or past the retention window";
            string effect = canExecuteWithoutList
                ? "the block executes without it after the retries"
                : "sync cannot pass this block until another peer serves it";
            _logger.Warn($"{(node.IsStatic ? "Static" : "Trusted")} peer {peer} did not serve the block access list of {header.ToString(BlockHeader.Format.Short)} ({reason}); {effect}.");
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

        /// <summary>Whether this header requires a BAL before suggestion.</summary>
        protected virtual bool ShouldDownloadAccessList(BlockHeader header, bool shouldProcess) => !shouldProcess;

        /// <summary>Whether the block access list of this header is already in the store, so that it is not downloaded.</summary>
        /// <remarks>Checked once, when the block enters the download buffer; processing takes a stored list from the store.</remarks>
        protected virtual bool IsAccessListStored(BlockHeader header) => false;

        /// <summary>Whether this header requires receipts before suggestion.</summary>
        protected virtual bool ShouldDownloadReceipts(BlockHeader header, bool shouldProcess, bool requested) => requested;

        /// <summary>
        /// Returns a block already stored locally for <paramref name="header"/>, so that its body is not downloaded again.
        /// </summary>
        /// <returns>The stored block, or <c>null</c> when the body has to be downloaded from a peer.</returns>
        protected virtual Block? FindLocalBlock(BlockHeader header) => null;

        private Block? TryGetLocalBlock(BlockHeader header)
        {
            if (FindLocalBlock(header) is not { } localBlock) return null;

            // Only check binding a stored body to its header: IsValidDownloadedBlock skips the roots (validateHashes: false),
            // and a beacon body can mismatch its header, e.g. a Taiko payload with null Transactions/Withdrawals stored under the wire roots.
            if (!_blockValidator.ValidateBodyAgainstHeader(header, localBlock.Body, out string? errorMessage))
            {
                if (_logger.IsDebug) _logger.Debug($"Stored body of {header.ToString(BlockHeader.Format.Short)} does not match its header, downloading it instead: {errorMessage}");
                return null;
            }

            return new Block(header, localBlock.Body);
        }

        protected virtual BlockTreeSuggestOptions GetSuggestOption(bool shouldProcess, Block currentBlock) =>
            shouldProcess
                ? BlockTreeSuggestOptions.ShouldProcess | BlockTreeSuggestOptions.NonBlockingProcessing
                : BlockTreeSuggestOptions.None;

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
            // Processing can start before SuggestBlock returns, and BAL reconstruction reads these receipts.
            if (shouldProcess && downloadReceipts && receipts is not null)
                _receiptStorage.Insert(currentBlock, receipts, ensureCanonical: false);

            AddBlockResult addResult = _blockTree.SuggestBlock(currentBlock, suggestOptions);
            bool handled = HandleAddResult(bestPeer, currentBlock.Header, isFirstInBatch, addResult);

            if (!shouldProcess && downloadReceipts && addResult is AddBlockResult.Added or AddBlockResult.AlreadyKnown)
            {
                if (receipts is not null)
                {
                    _receiptStorage.Insert(currentBlock, receipts);
                }
                else if (currentBlock.Header.HasTransactions)
                {
                    if (_logger.IsError) _logger.Error($"{currentBlock} is missing receipts");
                }
            }

            if (!shouldProcess)
            {
                if (!_blockTree.TryUpdateMainChain(currentBlock.Header, wereProcessed: false, preloadedBlocks: [currentBlock]))
                {
                    if (_logger.IsDebug) _logger.Debug($"Canonical update deferred for {currentBlock.Header.ToString(BlockHeader.Format.Short)}: a predecessor is missing or chain maintenance overlapped.");
                }
            }

            _forwardHeaderProvider.OnSuggestBlock(suggestOptions, currentBlock, addResult);

            return handled;
        }

        private (bool shouldProcess, bool shouldDownloadReceipt) ReceiptEdgeCase(
            ulong firstBlockNumber,
            bool shouldProcess,
            bool shouldDownloadReceipt)
        {
            if (shouldProcess && !shouldDownloadReceipt)
            {
                // Persisted state can sit ahead of the head after a fast-sync transition or a crash. Blocks at
                // or below it already have their post-state; processing them finds no parent state, throws, and
                // deletes the branch. Download receipts instead.
                ulong headNumber = _blockTree.Head?.Number ?? 0;
                ulong bestFullState = _fullStateFinder.FindBestFullState();
                bool persistedStateAheadOfHead = bestFullState > headNumber;
                if (persistedStateAheadOfHead)
                {
                    shouldProcess = firstBlockNumber > bestFullState;
                    if (!shouldProcess)
                    {
                        if (_logger.IsInfo) _logger.Info($"Turning on receipt download in full sync, currentBlock: {firstBlockNumber}, bestFullState: {bestFullState}, trying to load receipts");
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
                        throw new BlockTreeNotReadyException($"Block tree cannot accept block/header from peer {peerInfo}: #{block.Number} ({block.Hash}, parent {block.ParentHash})");
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
            private const int MissingDataRetries = 3;
            private int _missingAccessLists;
            private int _missingReceipts;
            public void RecordMissingAccessList()
            {
                if (CanExecuteMissingData && ++_missingAccessLists >= MissingDataRetries) SkipAccessListDownload = true;
            }
            public void RecordMissingReceipt()
            {
                if (CanExecuteMissingData && ++_missingReceipts >= MissingDataRetries) SkipReceiptDownload = true;
            }
            public void ResetMissingAccessList() { _missingAccessLists = 0; SkipAccessListDownload = false; }
            public void ResetMissingReceipt() { _missingReceipts = 0; SkipReceiptDownload = false; }
            public bool CanExecuteMissingData { get; set; }
            public bool SkipAccessListDownload { get; set; }
            public bool NoAccessListPeer { get; set; }
            public bool AccessListStored { get; init; }
            public bool SkipReceiptDownload { get; set; }
            public bool HasAccessList => AccessListStored || (CanExecuteMissingData && (SkipAccessListDownload || NoAccessListPeer)) || Header.BlockAccessListHash is null || EncodedAccessList is not null || Block?.BlockAccessList is not null;
            public bool HasReceipt => (CanExecuteMissingData && (SkipReceiptDownload || SkipAccessListDownload || NoAccessListPeer)) || !Header.HasTransactions || Receipts?.Length > 0;
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
