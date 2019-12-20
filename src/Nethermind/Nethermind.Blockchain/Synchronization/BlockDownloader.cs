//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Reflection.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization.SyncLimits;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.HashLib;
using Nethermind.Logging;
using Nethermind.Mining;

namespace Nethermind.Blockchain.Synchronization
{
    internal class BlockDownloader
    {
        public const int MaxReorganizationLength = SyncBatchSize.Max * 2;

        private readonly IBlockTree _blockTree;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly ISyncReport _syncReport;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        private SyncBatchSize _syncBatchSize;
        private int _sinceLastTimeout;
        private int[] _ancestorJumps = {1, 2, 3, 8, 16, 32, 64, 128, 256, 384, 512, 640, 768, 896, 1024};

        public BlockDownloader(IBlockTree blockTree,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ISyncReport syncReport,
            IReceiptStorage receiptStorage,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
            _syncReport = syncReport ?? throw new ArgumentNullException(nameof(syncReport));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _syncBatchSize = new SyncBatchSize(logManager);
        }

        public async Task<long> DownloadHeaders(PeerInfo bestPeer, int newBlocksToSkip, CancellationToken cancellation)
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
                if (_logger.IsTrace) _logger.Trace($"Continue headers sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");

                long blocksLeft = bestPeer.HeadNumber - currentNumber - newBlocksToSkip;
                int headersToRequest = (int) Math.Min(blocksLeft + 1, _syncBatchSize.Current);
                if (headersToRequest <= 1)
                {
                    break;
                }

                if (_logger.IsTrace) _logger.Trace($"Headers request {currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");
                BlockHeader[] headers = await RequestHeaders(bestPeer, cancellation, currentNumber, headersToRequest);

                BlockHeader startingPoint = headers[0] == null ? null : _blockTree.FindHeader(headers[0].Hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (startingPoint == null)
                {
                    ancestorLookupLevel++;
                    if (ancestorLookupLevel >= _ancestorJumps.Length)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {bestPeer}");
                        throw new EthSynchronizationException("Peer with inconsistent chain in sync");
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

                    BlockHeader currentHeader = headers[i];
                    if (currentHeader == null)
                    {
                        if (headersSynced > 0)
                        {
                            break;
                        }

                        return 0;
                    }

                    if (_logger.IsTrace) _logger.Trace($"Received {currentHeader} from {bestPeer:s}");
                    bool isValid = i > 1 ? _blockValidator.ValidateHeader(currentHeader, headers[i - 1], false) : _blockValidator.ValidateHeader(currentHeader, false);
                    if (!isValid)
                    {
                        throw new EthSynchronizationException($"{bestPeer} sent a block {currentHeader.ToString(BlockHeader.Format.Short)} with an invalid header");
                    }

                    if (HandleAddResult(currentHeader, i == 0, _blockTree.SuggestHeader(currentHeader)))
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

        public async Task<long> DownloadBlocks(PeerInfo bestPeer, int newBlocksToSkip, CancellationToken cancellation, BlockDownloaderOptions options = BlockDownloaderOptions.DownloadAndProcess)
        {
            bool downloadReceipts = options == BlockDownloaderOptions.DownloadWithReceipts;
            bool shouldProcess = options == BlockDownloaderOptions.DownloadAndProcess;

            if (bestPeer == null)
            {
                string message = $"Not expecting best peer to be null inside the {nameof(BlockDownloader)}";
                _logger.Error(message);
                throw new ArgumentNullException(message);
            }

            int blocksSynced = 0;
            int ancestorLookupLevel = 0;

            long currentNumber = Math.Max(0, Math.Min(_blockTree.BestKnownNumber, bestPeer.HeadNumber - 1));
            while (bestPeer.TotalDifficulty > (_blockTree.BestSuggestedHeader?.TotalDifficulty ?? 0) && currentNumber <= bestPeer.HeadNumber)
            {
                if (_logger.IsDebug) _logger.Debug($"Continue full sync with {bestPeer} (our best {_blockTree.BestKnownNumber})");

                long blocksLeft = bestPeer.HeadNumber - currentNumber - newBlocksToSkip;
                int headersToRequest = (int) Math.Min(blocksLeft + 1, _syncBatchSize.Current);
                if (headersToRequest <= 1)
                {
                    break;
                }

                headersToRequest = Math.Min(headersToRequest, bestPeer.MaxHeadersPerRequest());

                if (_logger.IsTrace) _logger.Trace($"Full sync request {currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");

                if (cancellation.IsCancellationRequested) return blocksSynced; // check before every heavy operation
                BlockHeader[] headers = await RequestHeaders(bestPeer, cancellation, currentNumber, headersToRequest);
                BlockDownloadContext context = new BlockDownloadContext(_specProvider, bestPeer, headers, downloadReceipts);

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
                if (context.FullBlocksCount > 0)
                {
                    bool parentIsKnown = _blockTree.IsKnownBlock(blocks[0].Number - 1, blocks[0].ParentHash);
                    if (!parentIsKnown)
                    {
                        ancestorLookupLevel++;
                        if (ancestorLookupLevel >= _ancestorJumps.Length)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {bestPeer}");
                            throw new EthSynchronizationException("Peer with inconsistent chain in sync");
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

                    if (_logger.IsTrace) _logger.Trace($"Received {blocks[blockIndex]} from {bestPeer}");

                    // can move this to block tree now?
                    if (!_blockValidator.ValidateSuggestedBlock(blocks[blockIndex]))
                    {
                        throw new EthSynchronizationException($"{bestPeer} sent an invalid block {blocks[blockIndex].ToString(Block.Format.Short)}.");
                    }

                    if (HandleAddResult(blocks[blockIndex].Header, blockIndex == 0, _blockTree.SuggestBlock(blocks[blockIndex], shouldProcess)))
                    {
                        if (downloadReceipts)
                        {
                            for (int receiptIndex = 0; receiptIndex < (context.ReceiptsForBlocks[blockIndex]?.Length ?? 0); receiptIndex++)
                            {
                                _receiptStorage.Add(context.ReceiptsForBlocks[blockIndex][receiptIndex], true);
                            }
                        }

                        blocksSynced++;
                    }

                    currentNumber = currentNumber + 1;
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
                if (downloadTask.Exception?.InnerException is TimeoutException
                    || (downloadTask.Exception?.InnerExceptions.Any(x => x is TimeoutException) ?? false)
                    || (downloadTask.Exception?.InnerExceptions.Any(x => x.InnerException is TimeoutException) ?? false))
                {
                    if (_logger.IsTrace) _logger.Error($"Failed to retrieve {entities} when synchronizing (Timeout)", downloadTask.Exception);
                    _syncBatchSize.Shrink();
                }
                else
                {
                    Exception exception = downloadTask.Exception;
                    AggregateException aggregateException = exception as AggregateException;
                    if (aggregateException != null)
                    {
                        exception = aggregateException.InnerExceptions[0];
                    }

                    if (_logger.IsInfo) _logger.Error($"Failed to retrieve {entities} when synchronizing.", exception);
                }

                throw new EthSynchronizationException($"{entities} task faulted", downloadTask.Exception);
            }

            return default;
        }

        private async Task<BlockHeader[]> RequestHeaders(PeerInfo peer, CancellationToken cancellation, long currentNumber, int headersToRequest)
        {
            Task<BlockHeader[]> headersRequest = peer.SyncPeer.GetBlockHeaders(currentNumber, headersToRequest, 0, cancellation);
            await headersRequest.ContinueWith(t => DownloadFailHandler(t, "headers"), cancellation);

            cancellation.ThrowIfCancellationRequested();

            BlockHeader[] headers = headersRequest.Result;
            ValidateSeals(cancellation, headers);
            ValidateBatchConsistency(peer, headers);
            return headers;
        }

        private async Task RequestBodies(PeerInfo peer, CancellationToken cancellation, BlockDownloadContext context)
        {
            int offset = 0;
            while (offset != context.NonEmptyBlockHashes.Count)
            {
                IList<Keccak> hashesToRequest = context.GetHashesByOffset(offset, peer.MaxBodiesPerRequest());
                Task<BlockBody[]> getBodiesRequest = peer.SyncPeer.GetBlockBodies(hashesToRequest, cancellation);
                await getBodiesRequest.ContinueWith(t => DownloadFailHandler(getBodiesRequest, "bodies"));
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
                await request.ContinueWith(t => DownloadFailHandler(request, "bodies"));

                TxReceipt[][] result = request.Result;
                for (int i = 0; i < result.Length; i++)
                {
                    context.SetReceipts(i + offset, result[i]);
                }

                offset += result.Length;
            }
        }

        private void ValidateBatchConsistency(PeerInfo bestPeer, BlockHeader[] headers)
        {
            // in the past (version 1.11) and possibly now too Parity was sending non canonical blocks in responses
            // so we need to confirm that the blocks form a valid subchain
            for (int i = 0; i < headers.Length; i++)
            {
                if (i != 0 && headers[i] != null && headers[i]?.ParentHash != headers[i - 1]?.Hash)
                {
                    if (_logger.IsTrace) _logger.Trace($"Inconsistent block list from peer {bestPeer}");
                    throw new EthSynchronizationException("Peer sent an inconsistent block list");
                }
            }
        }

        private void ValidateSeals(CancellationToken cancellation, BlockHeader[] headers)
        {
            if (_logger.IsTrace) _logger.Trace("Starting seal validation");
            ConcurrentQueue<Exception> exceptions = new ConcurrentQueue<Exception>();
            Parallel.For(0, headers.Length, (i, state) =>
            {
                if (cancellation.IsCancellationRequested)
                {
                    if (_logger.IsTrace) _logger.Trace("Returning fom seal validation");
                    state.Stop();
                    return;
                }

                BlockHeader header = headers[i];
                if (header == null)
                {
                    return;
                }

                try
                {
                    if (!_sealValidator.ValidateSeal(headers[i]))
                    {
                        if (_logger.IsTrace) _logger.Trace("One of the seals is invalid");
                        throw new EthSynchronizationException("Peer sent a block with an invalid seal");
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

        private bool HandleAddResult(BlockHeader block, bool isFirstInBatch, AddBlockResult addResult)
        {
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
                        throw new EthSynchronizationException(message);
                    }
                    else
                    {
                        const string message = "Peer sent an inconsistent batch of blocks/headers";
                        _logger.Error(message);
                        throw new EthSynchronizationException(message);
                    }
                }

                case AddBlockResult.CannotAccept:
                    throw new EthSynchronizationException("Block tree rejected block/header");
                case AddBlockResult.InvalidBlock:
                    throw new EthSynchronizationException("Peer sent an invalid block/header");
                case AddBlockResult.Added:
                    if (_logger.IsTrace) _logger.Trace($"Block/header {block.Number} suggested for processing");
                    return true;
                case AddBlockResult.AlreadyKnown:
                    if (_logger.IsTrace) _logger.Trace($"Block/header {block.Number} skipped - already known");
                    return false;
                default:
                    throw new NotImplementedException($"Unknown {nameof(AddBlockResult)} {addResult}");
            }
        }
    }
}