//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Synchronization.Blocks
{
    internal class BlockDownloader : SyncDispatcher<BlocksRequest?>
    {
        public const int MaxReorganizationLength = SyncBatchSize.Max * 2;

        private readonly IBlockTree _blockTree;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly ISyncReport _syncReport;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        private bool _cancelDueToBetterPeer;
        private AllocationWithCancellation _allocationWithCancellation;

        private SyncBatchSize _syncBatchSize;
        private int _sinceLastTimeout;
        private readonly int[] _ancestorJumps = {1, 2, 3, 8, 16, 32, 64, 128, 256, 384, 512, 640, 768, 896, 1024};

        public BlockDownloader(
            ISyncFeed<BlocksRequest?>? feed,
            ISyncPeerPool? syncPeerPool,
            IBlockTree? blockTree,
            IBlockValidator? blockValidator,
            ISealValidator? sealValidator,
            ISyncReport? syncReport,
            IReceiptStorage? receiptStorage,
            ISpecProvider? specProvider,
            ILogManager? logManager)
            : base(feed, syncPeerPool, new BlocksSyncPeerAllocationStrategyFactory(), logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _receiptsRecovery = new ReceiptsRecovery(new EthereumEcdsa(_specProvider.ChainId, logManager), _specProvider);
            _syncBatchSize = new SyncBatchSize(logManager);
            _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
        }

        private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            if (e.Block is null)
            {
                if(_logger.IsError) _logger.Error("Received a new block head which is null.");
                return;
            }
            
            _syncReport.FullSyncBlocksDownloaded.Update(_blockTree.BestSuggestedHeader?.Number ?? 0);
            _syncReport.FullSyncBlocksKnown = Math.Max(_syncReport.FullSyncBlocksKnown, e.Block.Number);
        }

        protected override async Task Dispatch(
            PeerInfo bestPeer,
            BlocksRequest? blocksRequest,
            CancellationToken cancellation)
        {
            if (blocksRequest == null)
            {
                if (Logger.IsWarn) Logger.Warn($"NULL received for dispatch in {nameof(BlockDownloader)}");
                return;
            }

            if (!_blockTree.CanAcceptNewBlocks) return;
            CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellation,
                    _allocationWithCancellation.Cancellation.Token);

            try
            {
                SyncEvent?.Invoke(this, new SyncEventArgs(bestPeer.SyncPeer, Synchronization.SyncEvent.Started));
                if ((blocksRequest.Options & DownloaderOptions.WithBodies) == DownloaderOptions.WithBodies)
                {
                    if (Logger.IsDebug) Logger.Debug("Downloading bodies");
                    await DownloadBlocks(bestPeer, blocksRequest, linkedCancellation.Token)
                        .ContinueWith(t => HandleSyncRequestResult(t, bestPeer), cancellation);
                    if (Logger.IsDebug) Logger.Debug("Finished downloading bodies");
                }
                else
                {
                    if (Logger.IsDebug) Logger.Debug("Downloading headers");
                    await DownloadHeaders(bestPeer, blocksRequest, linkedCancellation.Token)
                        .ContinueWith(t => HandleSyncRequestResult(t, bestPeer), cancellation);
                    if (Logger.IsDebug) Logger.Debug("Finished downloading headers");
                }
            }
            finally
            {
                _allocationWithCancellation.Dispose();
                linkedCancellation.Dispose();
            }
        }

        public async Task<long> DownloadHeaders(PeerInfo bestPeer, BlocksRequest blocksRequest, CancellationToken cancellation)
        {
            if (bestPeer == null)
            {
                string message = $"Not expecting best peer to be null inside the {nameof(BlockDownloader)}";
                _logger.Error(message);
                throw new ArgumentNullException(message);
            }

            int headersSynced = 0;
            int ancestorLookupLevel = 0;

            long currentNumber = Math.Max(0, Math.Min(_blockTree.BestKnownNumber, bestPeer.HeadNumber - 1));
            while (bestPeer.TotalDifficulty > (_blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0) && currentNumber <= bestPeer.HeadNumber)
            {
                int headersSyncedInPreviousRequests = headersSynced;
                if (_logger.IsTrace) _logger.Trace($"Continue headers sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");

                long blocksLeft = bestPeer.HeadNumber - currentNumber - (blocksRequest.NumberOfLatestBlocksToBeIgnored ?? 0);
                int headersToRequest = (int) Math.Min(blocksLeft + 1, _syncBatchSize.Current);
                if (headersToRequest <= 1)
                {
                    break;
                }

                if (_logger.IsDebug) _logger.Debug($"Headers request {currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");
                BlockHeader?[] headers = await RequestHeaders(bestPeer, cancellation, currentNumber, headersToRequest);

                Keccak? startHeaderHash = headers[0]?.Hash;
                BlockHeader? startHeader = (startHeaderHash is null)
                    ? null : _blockTree.FindHeader(startHeaderHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (startHeader is null)
                {
                    ancestorLookupLevel++;
                    if (ancestorLookupLevel >= _ancestorJumps.Length)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {bestPeer}");
                        throw new EthSyncException("Peer with inconsistent chain in sync");
                    }

                    int ancestorJump = _ancestorJumps[ancestorLookupLevel] - _ancestorJumps[ancestorLookupLevel - 1];
                    currentNumber = currentNumber >= ancestorJump ? (currentNumber - ancestorJump) : 0L;
                    continue;
                }

                ancestorLookupLevel = 0;
                _sinceLastTimeout++;
                if (_sinceLastTimeout >= 2)
                {
                    // if peers are not timing out then we can try to be slightly more eager
                    _syncBatchSize.Expand();
                }

                for (int i = 1; i < headers.Length; i++)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    BlockHeader? currentHeader = headers[i];
                    if (currentHeader == null)
                    {
                        if (headersSynced - headersSyncedInPreviousRequests > 0)
                        {
                            break;
                        }

                        SyncPeerPool.ReportNoSyncProgress(bestPeer, AllocationContexts.Blocks);
                        return 0;
                    }

                    if (_logger.IsTrace) _logger.Trace($"Received {currentHeader} from {bestPeer:s}");
                    bool isValid = i > 1 ? _blockValidator.Validate(currentHeader, headers[i - 1]) : _blockValidator.Validate(currentHeader);
                    if (!isValid)
                    {
                        throw new EthSyncException($"{bestPeer} sent a block {currentHeader.ToString(BlockHeader.Format.Short)} with an invalid header");
                    }

                    // i == 0 is always false but leave it this was as it will be possible that we will change the 
                    // loop iterator to start with o
                    if (HandleAddResult(bestPeer, currentHeader, i == 0, _blockTree.Insert(currentHeader)))
                    {
                        headersSynced++;
                    }

                    currentNumber = currentNumber + 1;
                }

                if (headersSynced > 0)
                {
                    _syncReport.FullSyncBlocksDownloaded.Update(_blockTree.BestSuggestedHeader?.Number ?? 0);
                    _syncReport.FullSyncBlocksKnown = bestPeer.HeadNumber;
                }
                else
                {
                    break;
                }
            }

            return headersSynced;
        }

        public async Task<long> DownloadBlocks(PeerInfo? bestPeer, BlocksRequest blocksRequest, CancellationToken cancellation)
        {
            if (bestPeer == null)
            {
                string message = $"Not expecting best peer to be null inside the {nameof(BlockDownloader)}";
                if (_logger.IsError) _logger.Error(message);
                throw new ArgumentNullException(message);
            }

            DownloaderOptions options = blocksRequest.Options;
            bool downloadReceipts = (options & DownloaderOptions.WithReceipts) == DownloaderOptions.WithReceipts;
            bool shouldProcess = (options & DownloaderOptions.Process) == DownloaderOptions.Process;
            bool shouldMoveToMain = (options & DownloaderOptions.MoveToMain) == DownloaderOptions.MoveToMain;

            int blocksSynced = 0;
            int ancestorLookupLevel = 0;

            long currentNumber = Math.Max(0, Math.Min(_blockTree.BestKnownNumber, bestPeer.HeadNumber - 1));
            // pivot number - 6 for uncle validation
            // long currentNumber = Math.Max(Math.Max(0, pivotNumber - 6), Math.Min(_blockTree.BestKnownNumber, bestPeer.HeadNumber - 1));

            while (bestPeer.TotalDifficulty > (_blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0) && currentNumber <= bestPeer.HeadNumber)
            {
                if (_logger.IsDebug) _logger.Debug($"Continue full sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");

                long blocksLeft = bestPeer.HeadNumber - currentNumber - (blocksRequest.NumberOfLatestBlocksToBeIgnored ?? 0);
                int headersToRequest = (int) Math.Min(blocksLeft + 1, _syncBatchSize.Current);
                if (headersToRequest <= 1)
                {
                    break;
                }

                headersToRequest = Math.Min(headersToRequest, bestPeer.MaxHeadersPerRequest());
                if (_logger.IsTrace) _logger.Trace($"Full sync request {currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");

                if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                BlockHeader[] headers = await RequestHeaders(bestPeer, cancellation, currentNumber, headersToRequest);
                BlockDownloadContext context = new(_specProvider, bestPeer, headers, downloadReceipts, _receiptsRecovery);

                if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                await RequestBodies(bestPeer, cancellation, context);

                if (downloadReceipts)
                {
                    if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                    await RequestReceipts(bestPeer, cancellation, context);
                }

                _sinceLastTimeout++;
                if (_sinceLastTimeout > 2)
                {
                    _syncBatchSize.Expand();
                }

                Block[] blocks = context.Blocks;
                Block blockZero = blocks[0];
                if (context.FullBlocksCount > 0)
                {
                    bool parentIsKnown = _blockTree.IsKnownBlock(blockZero.Number - 1, blockZero.ParentHash);
                    if (!parentIsKnown)
                    {
                        ancestorLookupLevel++;
                        if (ancestorLookupLevel >= _ancestorJumps.Length)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {bestPeer}");
                            throw new EthSyncException("Peer with inconsistent chain in sync");
                        }

                        int ancestorJump = _ancestorJumps[ancestorLookupLevel] - _ancestorJumps[ancestorLookupLevel - 1];
                        currentNumber = currentNumber >= ancestorJump ? (currentNumber - ancestorJump) : 0L;
                        continue;
                    }
                }

                ancestorLookupLevel = 0;
                for (int blockIndex = 0; blockIndex < context.FullBlocksCount; blockIndex++)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
                        break;
                    }

                    Block currentBlock = blocks[blockIndex];
                    if (_logger.IsTrace) _logger.Trace($"Received {currentBlock} from {bestPeer}");

                    // can move this to block tree now?
                    if (!_blockValidator.ValidateSuggestedBlock(currentBlock))
                    {
                        throw new EthSyncException($"{bestPeer} sent an invalid block {currentBlock.ToString(Block.Format.Short)}.");
                    }

                    if (downloadReceipts)
                    {
                        TxReceipt[]? contextReceiptsForBlock = context.ReceiptsForBlocks![blockIndex];
                        if (currentBlock.Header.HasBody && contextReceiptsForBlock == null)
                        {
                            throw new EthSyncException($"{bestPeer} didn't send receipts for block {currentBlock.ToString(Block.Format.Short)}.");
                        }
                    }

                    if (HandleAddResult(bestPeer, currentBlock.Header, blockIndex == 0, _blockTree.SuggestBlock(currentBlock, shouldProcess)))
                    {
                        if (downloadReceipts)
                        {
                            TxReceipt[]? contextReceiptsForBlock = context.ReceiptsForBlocks![blockIndex];
                            if (contextReceiptsForBlock != null)
                            {
                                _receiptStorage.Insert(currentBlock, contextReceiptsForBlock);
                            }
                            else
                            {
                                // this shouldn't now happen with new validation above, still lets keep this check 
                                if (currentBlock.Header.HasBody)
                                {
                                    if (_logger.IsError) _logger.Error($"{currentBlock} is missing receipts");
                                }
                            }
                        }

                        blocksSynced++;
                    }

                    if (shouldMoveToMain)
                    {
                        _blockTree.UpdateMainChain(new[] {currentBlock}, false);
                    }

                    currentNumber += 1;
                }

                if (blocksSynced > 0)
                {
                    _syncReport.FullSyncBlocksDownloaded.Update(_blockTree.BestSuggestedHeader?.Number ?? 0);
                    _syncReport.FullSyncBlocksKnown = bestPeer.HeadNumber;
                }
                else
                {
                    break;
                }
            }

            return blocksSynced;
        }

        private ValueTask DownloadFailHandler<T>(Task<T> downloadTask, string entities)
        {
            if (downloadTask.IsFaulted)
            {
                _sinceLastTimeout = 0;
                if (downloadTask.Exception?.Flatten().InnerExceptions.Any(x => x is TimeoutException) ?? false)
                {
                    if (_logger.IsTrace) _logger.Error($"Failed to retrieve {entities} when synchronizing (Timeout)", downloadTask.Exception);
                    _syncBatchSize.Shrink();
                }

                if (downloadTask.Exception != null)
                {
                    _ = downloadTask.Result; // trying to throw with stack trace
                }
            }

            return default;
        }

        private readonly Guid _sealValidatorUserGuid = Guid.NewGuid();

        private async Task<BlockHeader[]> RequestHeaders(PeerInfo peer, CancellationToken cancellation, long currentNumber, int headersToRequest)
        {
            _sealValidator.HintValidationRange(_sealValidatorUserGuid, currentNumber - 1028, currentNumber + 30000);
            Task<BlockHeader[]> headersRequest = peer.SyncPeer.GetBlockHeaders(currentNumber, headersToRequest, 0, cancellation);
            await headersRequest.ContinueWith(t => DownloadFailHandler(t, "headers"), cancellation);

            cancellation.ThrowIfCancellationRequested();

            BlockHeader[] headers = headersRequest.Result;
            ValidateSeals(headers, cancellation);
            ValidateBatchConsistencyAndSetParents(peer, headers);
            return headers;
        }

        private async Task RequestBodies(PeerInfo peer, CancellationToken cancellation, BlockDownloadContext context)
        {
            int offset = 0;
            while (offset != context.NonEmptyBlockHashes.Count)
            {
                IList<Keccak> hashesToRequest = context.GetHashesByOffset(offset, peer.MaxBodiesPerRequest());
                Task<BlockBody[]> getBodiesRequest = peer.SyncPeer.GetBlockBodies(hashesToRequest, cancellation);
                await getBodiesRequest.ContinueWith(_ => DownloadFailHandler(getBodiesRequest, "bodies"), cancellation);
                BlockBody[] result = getBodiesRequest.Result;
                for (int i = 0; i < result.Length; i++)
                {
                    context.SetBody(i + offset, result[i]);
                }

                offset += result.Length;
            }
        }

        private async Task RequestReceipts(PeerInfo peer, CancellationToken cancellation, BlockDownloadContext context)
        {
            int offset = 0;
            while (offset != context.NonEmptyBlockHashes.Count)
            {
                IList<Keccak> hashesToRequest = context.GetHashesByOffset(offset, peer.MaxReceiptsPerRequest());
                Task<TxReceipt[][]> request = peer.SyncPeer.GetReceipts(hashesToRequest, cancellation);
                await request.ContinueWith(_ => DownloadFailHandler(request, "receipts"), cancellation);

                TxReceipt[][] result = request.Result;
                
                for (int i = 0; i < result.Length; i++)
                {
                    TxReceipt[] txReceipts = result[i];
                    if (!context.TrySetReceipts(i + offset, txReceipts, out Block block))
                    {
                        throw new EthSyncException($"{peer} sent invalid receipts for block {block.ToString(Block.Format.Short)}.");
                    }
                }

                if (result.Length == 0)
                {
                    throw new EthSyncException("Empty receipts response received");
                }

                offset += result.Length;
            }
        }

        private void ValidateBatchConsistencyAndSetParents(PeerInfo bestPeer, BlockHeader?[] headers)
        {
            // in the past (version 1.11) and possibly now too Parity was sending non canonical blocks in responses
            // so we need to confirm that the blocks form a valid subchain
            for (int i = 1; i < headers.Length; i++)
            {
                if (headers[i] != null && headers[i]?.ParentHash != headers[i - 1]?.Hash)
                {
                    if (_logger.IsTrace) _logger.Trace($"Inconsistent block list from peer {bestPeer}");
                    throw new EthSyncException("Peer sent an inconsistent block list");
                }

                if (headers[i] == null)
                {
                    break;
                }

                if (i != 1) // because we will never set TotalDifficulty on the first block?
                {
                    headers[i].MaybeParent = new WeakReference<BlockHeader>(headers[i - 1]);
                }
            }
        }

        private void ValidateSeals(BlockHeader?[] headers, CancellationToken cancellation)
        {
            if (_logger.IsTrace) _logger.Trace("Starting seal validation");
            ConcurrentQueue<Exception> exceptions = new();
            Parallel.For(0, headers.Length, (i, state) =>
            {
                if (cancellation.IsCancellationRequested)
                {
                    if (_logger.IsTrace) _logger.Trace("Returning fom seal validation");
                    state.Stop();
                    return;
                }

                BlockHeader? header = headers[i];
                if (header is null)
                {
                    return;
                }

                try
                {
                    if (!_sealValidator.ValidateSeal(header, false))
                    {
                        if (_logger.IsTrace) _logger.Trace("One of the seals is invalid");
                        throw new EthSyncException("Peer sent a block with an invalid seal");
                    }
                }
                catch (Exception e)
                {
                    exceptions.Enqueue(e);
                    state.Stop();
                }
            });

            if (_logger.IsTrace) _logger.Trace("Seal validation complete");

            if (exceptions.Count > 0)
            {
                if (_logger.IsDebug) _logger.Debug("Seal validation failure");
                throw new AggregateException(exceptions);
            }
        }

        private bool HandleAddResult(PeerInfo peerInfo, BlockHeader block, bool isFirstInBatch, AddBlockResult addResult)
        {
            static void UpdatePeerInfo(PeerInfo peerInfo, BlockHeader header)
            {
                if (header.Hash is not null && header.TotalDifficulty is not null && header.TotalDifficulty > peerInfo.TotalDifficulty)
                {
                    peerInfo.SyncPeer.TotalDifficulty = header.TotalDifficulty.Value;
                    peerInfo.SyncPeer.HeadNumber = header.Number;
                    peerInfo.SyncPeer.HeadHash = header.Hash;
                }
            }

            switch (addResult)
            {
                // this generally should not happen as there is a consistency check before
                case AddBlockResult.UnknownParent:
                {
                    if (_logger.IsTrace) _logger.Trace($"Block/header {block.Number} ignored (unknown parent)");
                    if (isFirstInBatch)
                    {
                        const string message = "Peer sent orphaned blocks/headers inside the batch";
                        _logger.Error(message);
                        throw new EthSyncException(message);
                    }
                    else
                    {
                        const string message = "Peer sent an inconsistent batch of blocks/headers";
                        _logger.Error(message);
                        throw new EthSyncException(message);
                    }
                }
                case AddBlockResult.CannotAccept:
                    throw new EthSyncException("Block tree rejected block/header");
                case AddBlockResult.InvalidBlock:
                    throw new EthSyncException("Peer sent an invalid block/header");
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

        private void HandleSyncRequestResult(Task<long> task, PeerInfo? peerInfo)
        {
            switch (task)
            {
                case {IsFaulted: true} t:
                    string reason;
                    if (t.Exception != null && t.Exception.Flatten().InnerExceptions.Any(x => x is TimeoutException))
                    {
                        if (_logger.IsDebug) _logger.Debug($"Block download from {peerInfo} timed out. {t.Exception?.Message}");
                        reason = "timeout";
                    }
                    else if (t.Exception != null && t.Exception.Flatten().InnerExceptions.Any(x => x is TaskCanceledException))
                    {
                        if (_logger.IsDebug) _logger.Debug($"Block download from {peerInfo} was canceled.");
                        reason = "cancel";
                    }
                    else
                    {
                        if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Block download from {peerInfo} failed. {t.Exception}");
                        // ReSharper disable once RedundantAssignment
                        reason = $"sync fault";
#if DEBUG
                        reason = $"sync fault| {t.Exception}";
#endif
                    }

                    if (peerInfo is not null) // fix this for node data sync
                    {
                        peerInfo.SyncPeer.Disconnect(DisconnectReason.DisconnectRequested, reason);
                        // redirect sync event from block downloader here (move this one inside)
                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, Synchronization.SyncEvent.Failed));
                    }

                    break;
                case {IsCanceled: true}:
                    if (_cancelDueToBetterPeer)
                    {
                        _cancelDueToBetterPeer = false;
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"Blocks download from {peerInfo} canceled. Removing node from sync peers.");
                        if (peerInfo != null) // fix this for node data sync
                        {
                            SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, Synchronization.SyncEvent.Cancelled));
                        }
                    }

                    break;
                case {IsCompletedSuccessfully: true} t:
                    if (_logger.IsDebug) _logger.Debug($"Blocks download from {peerInfo} completed with progress {t.Result}.");
                    if (peerInfo != null) // fix this for node data sync
                    {
                        SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, Synchronization.SyncEvent.Completed));
                    }

                    break;
            }
        }

        protected override async Task<SyncPeerAllocation> Allocate(BlocksRequest? request)
        {
            if (request == null)
            {
                throw new InvalidOperationException($"NULL received for dispatch in {nameof(BlockDownloader)}");
            }

            SyncPeerAllocation allocation = await base.Allocate(request);
            CancellationTokenSource cancellation = new();
            _allocationWithCancellation = new AllocationWithCancellation(allocation, cancellation);

            allocation.Cancelled += AllocationOnCancelled;
            allocation.Replaced += AllocationOnReplaced;
            return allocation;
        }

        protected override void Free(SyncPeerAllocation allocation)
        {
            allocation.Cancelled -= AllocationOnCancelled;
            allocation.Replaced -= AllocationOnReplaced;
            base.Free(allocation);
        }

        private void AllocationOnCancelled(object? sender, AllocationChangeEventArgs e)
        {
            AllocationWithCancellation allocationWithCancellation = _allocationWithCancellation;
            if (allocationWithCancellation.Allocation != sender)
            {
                return;
            }

            allocationWithCancellation.Cancel();
        }

        private void AllocationOnReplaced(object? sender, AllocationChangeEventArgs e)
        {
            if (e.Previous == null)
            {
                if (_logger.IsDebug) _logger.Debug($"Allocating {e.Current} for the blocks sync allocation");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Replacing {e.Previous} with {e.Current} for the blocks sync allocation.");
            }

            if (e.Previous != null)
            {
                _cancelDueToBetterPeer = true;
                _allocationWithCancellation.Cancel();
            }

            PeerInfo? newPeer = e.Current;
            BlockHeader? bestSuggested = _blockTree.BestSuggestedHeader;
            if ((newPeer?.TotalDifficulty ?? 0) > (bestSuggested?.TotalDifficulty ?? 0))
            {
                Feed.Activate();
            }
        }

        private struct AllocationWithCancellation : IDisposable
        {
            public AllocationWithCancellation(SyncPeerAllocation allocation, CancellationTokenSource cancellation)
            {
                Allocation = allocation;
                Cancellation = cancellation;
                _isDisposed = false;
            }

            public CancellationTokenSource Cancellation { get; }
            public SyncPeerAllocation Allocation { get; }

            public void Cancel()
            {
                if (!_isDisposed)
                {
                    Cancellation.Cancel();
                }
            }

            private bool _isDisposed;

            public void Dispose()
            {
                if (!_isDisposed)
                {
                    _isDisposed = true;
                    Cancellation.Dispose();
                }
            }
        }
    }
}
