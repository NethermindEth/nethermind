// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;

namespace Nethermind.Synchronization.Blocks
{
    public class BlockDownloader : ISyncDownloader<BlocksRequest>
    {
        public const int MaxReorganizationLength = SyncBatchSize.Max * 2;

        // This includes both body and receipt
        public static readonly TimeSpan SyncBatchDownloadTimeUpperBound = TimeSpan.FromMilliseconds(8000);
        public static readonly TimeSpan SyncBatchDownloadTimeLowerBound = TimeSpan.FromMilliseconds(5000);

        private readonly ISyncFeed<BlocksRequest> _feed;
        private readonly IBlockTree _blockTree;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly ISyncReport _syncReport;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private readonly ISpecProvider _specProvider;
        private readonly IBetterPeerStrategy _betterPeerStrategy;
        private readonly ILogger _logger;
        private readonly ISyncPeerPool _syncPeerPool;
        private readonly Guid _sealValidatorUserGuid = Guid.NewGuid();
        private readonly Random _rnd = new();

        private bool _cancelDueToBetterPeer;
        protected AllocationWithCancellation _allocationWithCancellation = new(null, new CancellationTokenSource());
        protected bool HasBetterPeer => _allocationWithCancellation.IsCancellationRequested;

        protected SyncBatchSize _syncBatchSize;
        protected int _sinceLastTimeout;
        private readonly int[] _ancestorJumps = { 1, 2, 3, 8, 16, 32, 64, 128, 256, 384, 512, 640, 768, 896, 1024 };

        public BlockDownloader(
            ISyncFeed<BlocksRequest?>? feed,
            ISyncPeerPool? syncPeerPool,
            IBlockTree? blockTree,
            IBlockValidator? blockValidator,
            ISealValidator? sealValidator,
            ISyncReport? syncReport,
            IReceiptStorage? receiptStorage,
            ISpecProvider? specProvider,
            IBetterPeerStrategy betterPeerStrategy,
            ILogManager? logManager,
            SyncBatchSize? syncBatchSize = null)
        {
            _feed = feed;
            _syncPeerPool = syncPeerPool ?? throw new ArgumentNullException(nameof(syncPeerPool));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _betterPeerStrategy = betterPeerStrategy ?? throw new ArgumentNullException(nameof(betterPeerStrategy));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _receiptsRecovery = new ReceiptsRecovery(new EthereumEcdsa(_specProvider.ChainId, logManager), _specProvider);
            _syncBatchSize = syncBatchSize ?? new SyncBatchSize(logManager);
            _blockTree.NewHeadBlock += BlockTreeOnNewHeadBlock;
        }

        private void BlockTreeOnNewHeadBlock(object? sender, BlockEventArgs e)
        {
            if (e.Block is null)
            {
                if (_logger.IsError) _logger.Error("Received a new block head which is null.");
                return;
            }

            _syncReport.FullSyncBlocksDownloaded.Update(_blockTree.BestSuggestedHeader?.Number ?? 0);
            _syncReport.FullSyncBlocksKnown = Math.Max(_syncReport.FullSyncBlocksKnown, e.Block.Number);
        }

        protected PeerInfo? _previousBestPeer = null;

        public virtual async Task Dispatch(PeerInfo bestPeer, BlocksRequest? blocksRequest, CancellationToken cancellation)
        {
            if (blocksRequest is null)
            {
                if (_logger.IsWarn) _logger.Warn($"NULL received for dispatch in {nameof(BlockDownloader)}");
                return;
            }

            if (!_blockTree.CanAcceptNewBlocks) return;

            if (_previousBestPeer != bestPeer)
            {
                _syncBatchSize.Reset();
            }
            _previousBestPeer = bestPeer;

            try
            {
                SyncEvent?.Invoke(this, new SyncEventArgs(bestPeer.SyncPeer, Synchronization.SyncEvent.Started));
                if ((blocksRequest.Options & DownloaderOptions.WithBodies) == DownloaderOptions.WithBodies)
                {
                    if (_logger.IsDebug) _logger.Debug("Downloading bodies");
                    await DownloadBlocks(bestPeer, blocksRequest, cancellation)
                        .ContinueWith(t => HandleSyncRequestResult(t, bestPeer), cancellation);
                    if (_logger.IsDebug) _logger.Debug("Finished downloading bodies");
                }
                else
                {
                    if (_logger.IsDebug) _logger.Debug("Downloading headers");
                    await DownloadHeaders(bestPeer, blocksRequest, cancellation)
                        .ContinueWith(t => HandleSyncRequestResult(t, bestPeer), cancellation);
                    if (_logger.IsDebug) _logger.Debug("Finished downloading headers");
                }
            }
            finally
            {
                _allocationWithCancellation.Dispose();
            }
        }

        public async Task<long> DownloadHeaders(PeerInfo bestPeer, BlocksRequest blocksRequest, CancellationToken cancellation)
        {
            if (bestPeer is null)
            {
                string message = $"Not expecting best peer to be null inside the {nameof(BlockDownloader)}";
                _logger.Error(message);
                throw new ArgumentNullException(message);
            }

            int headersSynced = 0;
            int ancestorLookupLevel = 0;

            long currentNumber = Math.Max(0, Math.Min(_blockTree.BestKnownNumber, bestPeer.HeadNumber - 1));
            bool HasMoreToSync()
                => currentNumber <= bestPeer!.HeadNumber;
            while (ImprovementRequirementSatisfied(bestPeer) && HasMoreToSync())
            {
                if (HasBetterPeer) break;
                int headersSyncedInPreviousRequests = headersSynced;
                if (_logger.IsTrace) _logger.Trace($"Continue headers sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");

                long blocksLeft = bestPeer.HeadNumber - currentNumber - (blocksRequest.NumberOfLatestBlocksToBeIgnored ?? 0);
                int headersToRequest = (int)Math.Min(blocksLeft + 1, _syncBatchSize.Current);
                if (headersToRequest <= 1)
                {
                    break;
                }

                if (_logger.IsDebug) _logger.Debug($"Headers request {currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");
                Stopwatch sw = Stopwatch.StartNew();
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
                AdjustSyncBatchSize(sw.Elapsed);

                for (int i = 1; i < headers.Length; i++)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    BlockHeader? currentHeader = headers[i];
                    if (currentHeader is null)
                    {
                        if (headersSynced - headersSyncedInPreviousRequests > 0)
                        {
                            break;
                        }

                        _syncPeerPool.ReportNoSyncProgress(bestPeer, AllocationContexts.Blocks);
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
                        TryUpdateTerminalBlock(currentHeader, false);
                        headersSynced++;
                    }

                    currentNumber += 1;
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

        public virtual async Task<long> DownloadBlocks(PeerInfo? bestPeer, BlocksRequest blocksRequest,
            CancellationToken cancellation)
        {
            if (bestPeer is null)
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

            bool HasMoreToSync()
                => currentNumber <= bestPeer!.HeadNumber;
            while (ImprovementRequirementSatisfied(bestPeer!) && HasMoreToSync())
            {
                if (HasBetterPeer) break;
                if (_logger.IsDebug) _logger.Debug($"Continue full sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");

                long upperDownloadBoundary = bestPeer.HeadNumber - (blocksRequest.NumberOfLatestBlocksToBeIgnored ?? 0);
                long blocksLeft = upperDownloadBoundary - currentNumber;
                int headersToRequest = (int)Math.Min(blocksLeft + 1, _syncBatchSize.Current);
                if (headersToRequest <= 1)
                {
                    break;
                }

                headersToRequest = Math.Min(headersToRequest, bestPeer.MaxHeadersPerRequest());
                if (_logger.IsTrace) _logger.Trace($"Full sync request {currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");

                if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                BlockHeader[] headers = await RequestHeaders(bestPeer, cancellation, currentNumber, headersToRequest);
                if (headers.Length < 2)
                {
                    // Peer dont have new header
                    break;
                }

                BlockDownloadContext context = new(_specProvider, bestPeer, headers, downloadReceipts, _receiptsRecovery);

                if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation

                Stopwatch sw = Stopwatch.StartNew();
                await RequestBodies(bestPeer, cancellation, context);

                if (downloadReceipts)
                {
                    if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                    await RequestReceipts(bestPeer, cancellation, context);
                }

                AdjustSyncBatchSize(sw.Elapsed);

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

                    if (currentBlock.IsBodyMissing)
                    {
                        throw new EthSyncException($"{bestPeer} didn't send body for block {currentBlock.ToString(Block.Format.Short)}.");
                    }

                    // can move this to block tree now?
                    if (!_blockValidator.ValidateSuggestedBlock(currentBlock))
                    {
                        throw new EthSyncException($"{bestPeer} sent an invalid block {currentBlock.ToString(Block.Format.Short)}.");
                    }

                    if (downloadReceipts)
                    {
                        TxReceipt[]? contextReceiptsForBlock = context.ReceiptsForBlocks![blockIndex];
                        if (currentBlock.Header.HasTransactions && contextReceiptsForBlock is null)
                        {
                            throw new EthSyncException($"{bestPeer} didn't send receipts for block {currentBlock.ToString(Block.Format.Short)}.");
                        }
                    }

                    if (_logger.IsTrace) _logger.Trace($"BlockDownloader - SuggestBlock {currentBlock}, ShouldProcess: {true}");
                    if (HandleAddResult(bestPeer, currentBlock.Header, blockIndex == 0, _blockTree.SuggestBlock(currentBlock, shouldProcess ? BlockTreeSuggestOptions.ShouldProcess : BlockTreeSuggestOptions.None)))
                    {
                        TryUpdateTerminalBlock(currentBlock.Header, shouldProcess);
                        if (downloadReceipts)
                        {
                            TxReceipt[]? contextReceiptsForBlock = context.ReceiptsForBlocks![blockIndex];
                            if (contextReceiptsForBlock is not null)
                            {
                                _receiptStorage.Insert(currentBlock, contextReceiptsForBlock);
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

                        blocksSynced++;
                    }

                    if (shouldMoveToMain)
                    {
                        _blockTree.UpdateMainChain(new[] { currentBlock }, false);
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
        protected virtual bool ImprovementRequirementSatisfied(PeerInfo? bestPeer)
        {
            return bestPeer!.TotalDifficulty > (_blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0);
        }

        private ValueTask DownloadFailHandler<T>(Task<T> downloadTask, string entities)
        {
            if (downloadTask.IsFaulted)
            {
                if (downloadTask.HasTimeoutException())
                {
                    if (_logger.IsDebug) _logger.Error($"Failed to retrieve {entities} when synchronizing (Timeout)", downloadTask.Exception);
                    _syncBatchSize.Shrink();
                }

                if (downloadTask.Exception is not null)
                {
                    _ = downloadTask.GetAwaiter().GetResult(); // trying to throw with stack trace
                }
            }

            return default;
        }

        protected virtual async Task<BlockHeader[]> RequestHeaders(PeerInfo peer, CancellationToken cancellation, long currentNumber, int headersToRequest)
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

        protected async Task RequestBodies(PeerInfo peer, CancellationToken cancellation, BlockDownloadContext context)
        {
            int offset = 0;
            while (offset != context.NonEmptyBlockHashes.Count)
            {
                IReadOnlyList<Keccak> hashesToRequest = context.GetHashesByOffset(offset, peer.MaxBodiesPerRequest());
                Task<OwnedBlockBodies> getBodiesRequest = peer.SyncPeer.GetBlockBodies(hashesToRequest, cancellation);
                await getBodiesRequest.ContinueWith(_ => DownloadFailHandler(getBodiesRequest, "bodies"), cancellation);

                using OwnedBlockBodies ownedBlockBodies = getBodiesRequest.Result;
                ownedBlockBodies.Disown();
                BlockBody?[] result = ownedBlockBodies.Bodies;

                int receivedBodies = 0;
                for (int i = 0; i < result.Length; i++)
                {
                    if (result[i] is null)
                    {
                        break;
                    }
                    context.SetBody(i + offset, result[i]);
                    receivedBodies++;
                }

                if (receivedBodies == 0)
                {
                    if (_logger.IsTrace) _logger.Trace($"Peer sent no bodies. Peer: {peer}, Request: {hashesToRequest.Count}");
                    return;
                }

                offset += receivedBodies;
            }
        }

        protected async Task RequestReceipts(PeerInfo peer, CancellationToken cancellation, BlockDownloadContext context)
        {
            int offset = 0;
            while (offset != context.NonEmptyBlockHashes.Count)
            {
                IReadOnlyList<Keccak> hashesToRequest = context.GetHashesByOffset(offset, peer.MaxReceiptsPerRequest());
                Task<TxReceipt[][]> request = peer.SyncPeer.GetReceipts(hashesToRequest, cancellation);
                await request.ContinueWith(_ => DownloadFailHandler(request, "receipts"), cancellation);

                TxReceipt[][] result = request.Result;

                for (int i = 0; i < result.Length; i++)
                {
                    TxReceipt[] txReceipts = result[i];
                    Block block = context.GetBlockByRequestIdx(i + offset);
                    if (block.IsBodyMissing)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Found incomplete blocks. {block.Hash}");
                        return;
                    }
                    if (!context.TrySetReceipts(i + offset, txReceipts, out block))
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
                if (headers[i] is not null && headers[i]?.ParentHash != headers[i - 1]?.Hash)
                {
                    if (_logger.IsTrace) _logger.Trace($"Inconsistent block list from peer {bestPeer}");
                    throw new EthSyncException("Peer sent an inconsistent block list");
                }

                if (headers[i] is null)
                {
                    break;
                }

                if (i != 1) // because we will never set TotalDifficulty on the first block?
                {
                    headers[i].MaybeParent = new WeakReference<BlockHeader>(headers[i - 1]);
                }
            }
        }

        protected void ValidateSeals(BlockHeader?[] headers, CancellationToken cancellation)
        {
            if (_logger.IsTrace) _logger.Trace("Starting seal validation");
            ConcurrentQueue<Exception> exceptions = new();
            int randomNumberForValidation = _rnd.Next(Math.Max(0, headers.Length - 2));
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
                    bool lastBlock = i == headers.Length - 1;
                    // PoSSwitcher can't determine if a block is a terminal block if TD is missing due to another
                    // problem. In theory, this should not be a problem, but additional seal check does no harm.
                    bool terminalBlock = !lastBlock
                                         && headers.Length > 1
                                         && headers[i + 1].Difficulty == 0
                                         && headers[i].Difficulty != 0;
                    bool forceValidation = lastBlock || i == randomNumberForValidation || terminalBlock;
                    if (!_sealValidator.ValidateSeal(header, forceValidation))
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

            if (!exceptions.IsEmpty)
            {
                if (_logger.IsDebug) _logger.Debug("Seal validation failure");
                throw new AggregateException(exceptions);
            }
        }

        protected bool HandleAddResult(PeerInfo peerInfo, BlockHeader block, bool isFirstInBatch, AddBlockResult addResult)
        {
            void UpdatePeerInfo(PeerInfo peer, BlockHeader header)
            {
                if (header.Hash is not null && header.TotalDifficulty is not null && _betterPeerStrategy.Compare(header, peer?.SyncPeer) > 0)
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

        protected virtual void TryUpdateTerminalBlock(BlockHeader header, bool shouldProcess) { }

        public event EventHandler<SyncEventArgs>? SyncEvent;

        protected void InvokeEvent(SyncEventArgs args)
        {
            SyncEvent?.Invoke(this, args);
        }

        protected void HandleSyncRequestResult(Task<long> task, PeerInfo? peerInfo)
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
                        if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Block download from {peerInfo} failed. {t.Exception}");
                        // ReSharper disable once RedundantAssignment
                        reason = $"sync fault";
#if DEBUG
                        reason = $"sync fault| {t.Exception}";
#endif
                    }

                    if (peerInfo is not null) // fix this for node data sync
                    {
                        peerInfo.SyncPeer.Disconnect(DisconnectReason.ForwardSyncFailed, reason);
                        // redirect sync event from block downloader here (move this one inside)
                        InvokeEvent(new SyncEventArgs(peerInfo.SyncPeer, Synchronization.SyncEvent.Failed));
                    }

                    break;
                case { IsCanceled: true }:
                    if (_cancelDueToBetterPeer)
                    {
                        _cancelDueToBetterPeer = false;
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"Blocks download from {peerInfo} canceled. Removing node from sync peers.");
                        if (peerInfo is not null) // fix this for node data sync
                        {
                            InvokeEvent(new SyncEventArgs(peerInfo.SyncPeer, Synchronization.SyncEvent.Cancelled));
                        }
                    }

                    break;
                case { IsCompletedSuccessfully: true } t:
                    if (_logger.IsDebug) _logger.Debug($"Blocks download from {peerInfo} completed with progress {t.Result}.");
                    if (peerInfo is not null) // fix this for node data sync
                    {
                        InvokeEvent(new SyncEventArgs(peerInfo.SyncPeer, Synchronization.SyncEvent.Completed));
                    }

                    break;
            }
        }

        /// <summary>
        /// Adjust the sync batch size according to how much time it take to download the batch.
        /// </summary>
        /// <param name="downloadTime"></param>
        protected void AdjustSyncBatchSize(TimeSpan downloadTime)
        {
            // We shrink the batch size to prevent timeout. Timeout are wasted bandwith.
            if (downloadTime > SyncBatchDownloadTimeUpperBound)
            {
                _syncBatchSize.Shrink();
            }

            // We also want as high batch size as we can afford.
            if (downloadTime < SyncBatchDownloadTimeLowerBound)
            {
                _syncBatchSize.Expand();
            }
        }

        public void OnAllocate(SyncPeerAllocation allocation)
        {
            CancellationTokenSource cancellation = new();
            _allocationWithCancellation = new AllocationWithCancellation(allocation, cancellation);

            allocation.Cancelled += AllocationOnCancelled;
            allocation.Replaced += AllocationOnReplaced;
        }

        public void BeforeFree(SyncPeerAllocation allocation)
        {
            allocation.Cancelled -= AllocationOnCancelled;
            allocation.Replaced -= AllocationOnReplaced;
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
            if (e.Previous is null)
            {
                if (_logger.IsDebug) _logger.Debug($"Allocating {e.Current} for the blocks sync allocation");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Replacing {e.Previous} with {e.Current} for the blocks sync allocation.");
            }

            if (e.Previous is not null)
            {
                _cancelDueToBetterPeer = true;
                _allocationWithCancellation.Cancel();
            }

            PeerInfo? newPeer = e.Current;
            BlockHeader? bestSuggested = _blockTree.BestSuggestedHeader;
            if (_betterPeerStrategy.Compare(bestSuggested, newPeer?.SyncPeer) < 0)
            {
                _feed.Activate();
            }
        }

        protected struct AllocationWithCancellation : IDisposable
        {
            public AllocationWithCancellation(SyncPeerAllocation allocation, CancellationTokenSource cancellation)
            {
                Allocation = allocation;
                Cancellation = cancellation;
                _isDisposed = false;
            }

            private CancellationTokenSource Cancellation { get; }
            public bool IsCancellationRequested => Cancellation.IsCancellationRequested;
            public SyncPeerAllocation Allocation { get; }

            public void Cancel()
            {
                lock (Cancellation)
                {
                    if (!_isDisposed)
                    {
                        Cancellation.Cancel();
                    }
                }
            }

            private bool _isDisposed;

            public void Dispose()
            {
                lock (Cancellation)
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
}
