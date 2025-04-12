// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
        // This includes both body and receipt
        public static readonly TimeSpan SyncBatchDownloadTimeUpperBound = TimeSpan.FromMilliseconds(8000);
        public static readonly TimeSpan SyncBatchDownloadTimeLowerBound = TimeSpan.FromMilliseconds(5000);

        private readonly IBlockTree _blockTree;
        private readonly IBlockValidator _blockValidator;
        private readonly ISyncReport _syncReport;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private readonly ISpecProvider _specProvider;
        private readonly IBetterPeerStrategy _betterPeerStrategy;
        private readonly IFullStateFinder _fullStateFinder;
        private readonly IForwardHeaderProvider _forwardHeaderProvider;
        private readonly ILogger _logger;
        protected SyncBatchSize _syncBatchSize;

        public BlockDownloader(
            IBlockTree? blockTree,
            IBlockValidator? blockValidator,
            ISyncReport? syncReport,
            IReceiptStorage? receiptStorage,
            ISpecProvider? specProvider,
            IBetterPeerStrategy betterPeerStrategy,
            IFullStateFinder fullStateFinder,
            IForwardHeaderProvider forwardHeaderProvider,
            ILogManager? logManager,
            SyncBatchSize? syncBatchSize = null)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _betterPeerStrategy = betterPeerStrategy ?? throw new ArgumentNullException(nameof(betterPeerStrategy));
            _fullStateFinder = fullStateFinder ?? throw new ArgumentNullException(nameof(fullStateFinder));
            _forwardHeaderProvider = forwardHeaderProvider;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _receiptsRecovery = new ReceiptsRecovery(new EthereumEcdsa(_specProvider.ChainId), _specProvider);
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

            _syncReport.FullSyncBlocksDownloaded.TargetValue = Math.Max(_syncReport.FullSyncBlocksDownloaded.TargetValue, e.Block.Number);
            _syncReport.FullSyncBlocksDownloaded.Update(_blockTree.BestSuggestedHeader?.Number ?? 0);
        }

        private PeerInfo? _previousBestPeer = null;

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

            SyncEvent?.Invoke(this, new SyncEventArgs(bestPeer.SyncPeer, Synchronization.SyncEvent.Started));

            if (_logger.IsDebug) _logger.Debug("Downloading bodies");
            await DownloadBlocks(bestPeer, blocksRequest, cancellation)
                .ContinueWith(t => HandleSyncRequestResult(t, bestPeer), cancellation);
            if (_logger.IsDebug) _logger.Debug("Finished downloading bodies");
        }

        private async Task<long> DownloadBlocks(PeerInfo? bestPeer, BlocksRequest blocksRequest, CancellationToken cancellation)
        {
            if (bestPeer is null)
            {
                string message = $"Not expecting best peer to be null inside the {nameof(BlockDownloader)}";
                if (_logger.IsError) _logger.Error(message);
                throw new ArgumentNullException(message);
            }

            DownloaderOptions options = blocksRequest.Options;
            bool originalDownloadReceiptOpts = (options & DownloaderOptions.WithReceipts) == DownloaderOptions.WithReceipts;
            bool originalShouldProcess = (options & DownloaderOptions.Process) == DownloaderOptions.Process;

            int blocksSynced = 0;
            // pivot number - 6 for uncle validation
            // long currentNumber = Math.Max(Math.Max(0, pivotNumber - 6), Math.Min(_blockTree.BestKnownNumber, bestPeer.HeadNumber - 1));
            long bestProcessedBlock = 0;

            while (true)
            {
                using IOwnedReadOnlyList<BlockHeader?>? headers = await _forwardHeaderProvider.GetBlockHeaders(blocksRequest.NumberOfLatestBlocksToBeIgnored ?? 0, _syncBatchSize.Current, cancellation);
                if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                if (headers is null || headers.Count <= 1) return blocksSynced;

                (bool shouldProcess, bool downloadReceipts) = ReceiptEdgeCase(bestProcessedBlock, headers[0].Number, originalShouldProcess, originalDownloadReceiptOpts);

                BlockDownloadContext context = new(_specProvider, bestPeer, headers, downloadReceipts, _receiptsRecovery);
                long startTime = Stopwatch.GetTimestamp();
                await RequestBodies(bestPeer, cancellation, context);

                if (context.DownloadReceipts)
                {
                    if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                    await RequestReceipts(bestPeer, cancellation, context);
                }

                AdjustSyncBatchSize(Stopwatch.GetElapsedTime(startTime));

                Block[]? blocks = context.Blocks;
                TxReceipt[]?[]? receipts = context.ReceiptsForBlocks;

                if (!(blocks?.Length > 0))
                {
                    if (_logger.IsTrace)
                        _logger.Trace("Break early due to no blocks.");
                    break;
                }

                for (int blockIndex = 0; blockIndex < context.FullBlocksCount; blockIndex++)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
                        break;
                    }

                    Block currentBlock = blocks[blockIndex];
                    PreValidate(bestPeer, context, blockIndex);
                    if (SuggestBlock(bestPeer, currentBlock, blockIndex == 0, shouldProcess, context.DownloadReceipts, receipts?[blockIndex]))
                    {
                        if (shouldProcess)
                        {
                            bestProcessedBlock = currentBlock.Number;
                        }

                        blocksSynced++;
                    }
                }

                if (blocksSynced > 0)
                {
                    _syncReport.FullSyncBlocksDownloaded.Update(_blockTree.BestSuggestedHeader?.Number ?? 0);
                }
                else
                {
                    break;
                }
            }

            return blocksSynced;
        }

        protected virtual BlockTreeSuggestOptions GetSuggestOption(bool shouldProcess, Block currentBlock)
        {
            if (_logger.IsTrace) _logger.Trace($"BlockDownloader - SuggestBlock {currentBlock}, ShouldProcess: {true}");
            return shouldProcess ? BlockTreeSuggestOptions.ShouldProcess : BlockTreeSuggestOptions.None;
        }

        private bool SuggestBlock(
            PeerInfo bestPeer,
            Block currentBlock,
            bool isFirstInBatch,
            bool shouldProcess,
            bool downloadReceipts,
            TxReceipt[]? receipts)
        {
            BlockTreeSuggestOptions suggestOptions = GetSuggestOption(shouldProcess, currentBlock);
            AddBlockResult addResult = _blockTree.SuggestBlock(currentBlock, suggestOptions);
            bool handled = false;
            if (HandleAddResult(bestPeer, currentBlock.Header, isFirstInBatch, addResult))
            {
                if (!shouldProcess)
                {
                    _blockTree.UpdateMainChain(new[] { currentBlock }, false);
                }

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
                _blockTree.UpdateMainChain(new[] { currentBlock }, false);
            }

            _forwardHeaderProvider.OnSuggestBlock(suggestOptions, currentBlock, addResult);

            return handled;
        }

        private (bool shouldProcess, bool shouldDownloadReceipt) ReceiptEdgeCase(
            long bestProcessedBlock,
            long firstBlockNumber,
            bool shouldProcess,
            bool shouldDownloadReceipt)
        {
            if (shouldProcess && !shouldDownloadReceipt)
            {
                long firstBlock = firstBlockNumber;
                // TODO: Double check this condition
                // An edge case where we already have the state but are still downloading preceding blocks.
                // We cannot process such blocks, but we are still requested to process them via blocksRequest.Options.
                // Therefore, we detect this situation and switch from processing to receipts downloading.
                bool headIsGenesis = _blockTree.Head?.IsGenesis ?? false;
                bool toBeProcessedHasNoProcessedParent = firstBlock > (bestProcessedBlock + 1);
                bool isFastSyncTransition = headIsGenesis && toBeProcessedHasNoProcessedParent;
                if (isFastSyncTransition)
                {
                    long bestFullState = _fullStateFinder.FindBestFullState();
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

        private void PreValidate(PeerInfo bestPeer, BlockDownloadContext blockDownloadContext, int blockIndex)
        {
            Block currentBlock = blockDownloadContext.Blocks[blockIndex];
            if (_logger.IsTrace) _logger.Trace($"Received {currentBlock} from {bestPeer}");

            if (currentBlock.IsBodyMissing)
            {
                throw new EthSyncException($"{bestPeer} didn't send body for block {currentBlock.ToString(Block.Format.Short)}.");
            }

            // can move this to block tree now?
            if (!_blockValidator.ValidateSuggestedBlock(currentBlock, out string? errorMessage))
            {
                string message = InvalidBlockHelper.GetMessage(currentBlock, $"invalid block sent by peer. {errorMessage}") +
                                 $" PeerInfo {bestPeer}";
                if (_logger.IsWarn) _logger.Warn(message);
                throw new EthSyncException(message);
            }

            if (blockDownloadContext.DownloadReceipts)
            {
                TxReceipt[]? contextReceiptsForBlock = blockDownloadContext.ReceiptsForBlocks![blockIndex];
                if (currentBlock.Header.HasTransactions && contextReceiptsForBlock is null)
                {
                    throw new EthSyncException($"{bestPeer} didn't send receipts for block {currentBlock.ToString(Block.Format.Short)}.");
                }
            }
        }

        private ValueTask DownloadFailHandler<T>(Task<T> downloadTask, string entities)
        {
            if (downloadTask.IsFaulted)
            {
                if (downloadTask.HasTimeoutException())
                {
                    if (_logger.IsDebug) _logger.Error($"DEBUG/ERROR Failed to retrieve {entities} when synchronizing (Timeout)", downloadTask.Exception);
                    _syncBatchSize.Shrink();
                }

                if (downloadTask.Exception is not null)
                {
                    _ = downloadTask.GetAwaiter().GetResult(); // trying to throw with stack trace
                }
            }

            return default;
        }

        private async Task RequestBodies(PeerInfo peer, CancellationToken cancellation, BlockDownloadContext context)
        {
            int offset = 0;
            while (offset != context.NonEmptyBlockHashes.Count)
            {
                IReadOnlyList<Hash256> hashesToRequest = context.GetHashesByOffset(offset, peer.MaxBodiesPerRequest());
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

        private async Task RequestReceipts(PeerInfo peer, CancellationToken cancellation, BlockDownloadContext context)
        {
            int offset = 0;
            while (offset != context.NonEmptyBlockHashes.Count)
            {
                IReadOnlyList<Hash256> hashesToRequest = context.GetHashesByOffset(offset, peer.MaxReceiptsPerRequest());
                Task<IOwnedReadOnlyList<TxReceipt[]>> request = peer.SyncPeer.GetReceipts(hashesToRequest, cancellation);
                await request.ContinueWith(_ => DownloadFailHandler(request, "receipts"), cancellation);

                using IOwnedReadOnlyList<TxReceipt[]> result = request.Result;

                for (int i = 0; i < result.Count; i++)
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
                        throw new EthSyncException($"{peer} {peer.PeerClientType} sent invalid receipts for block {block.ToString(Block.Format.Short)}.");
                    }
                }

                if (result.Count == 0)
                {
                    throw new EthSyncException("Empty receipts response received");
                }

                offset += result.Count;
            }
        }

        private bool HandleAddResult(PeerInfo peerInfo, BlockHeader block, bool isFirstInBatch, AddBlockResult addResult)
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

        public event EventHandler<SyncEventArgs>? SyncEvent;

        private void InvokeEvent(SyncEventArgs args)
        {
            SyncEvent?.Invoke(this, args);
        }

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

        /// <summary>
        /// Adjust the sync batch size according to how much time it take to download the batch.
        /// </summary>
        /// <param name="downloadTime"></param>
        private void AdjustSyncBatchSize(TimeSpan downloadTime)
        {
            // We shrink the batch size to prevent timeout. Timeout are wasted bandwidth.
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
    }
}
