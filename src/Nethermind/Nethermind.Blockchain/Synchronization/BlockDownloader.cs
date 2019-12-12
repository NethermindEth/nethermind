/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Mining;

namespace Nethermind.Blockchain.Synchronization
{
    internal class BlockDownloader
    {
        public enum DownloadOptions
        {
            Download,
            DownloadAndProcess,
            DownloadWithReceipts
        }
        
        public const int MaxReorganizationLength = 2 * SyncBatchSize.Max;

        private readonly IBlockTree _blockTree;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly ISyncReport _syncReport;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        private SyncBatchSize _syncBatchSize;
        private int _sinceLastTimeout;

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

                if (ancestorLookupLevel > MaxReorganizationLength)
                {
                    if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {bestPeer}");
                    throw new EthSynchronizationException("Peer with inconsistent chain in sync");
                }

                long blocksLeft = bestPeer.HeadNumber - currentNumber - newBlocksToSkip;
                int headersToRequest = (int) BigInteger.Min(blocksLeft + 1, _syncBatchSize.Current);
                if (headersToRequest <= 1)
                {
                    break;
                }

                if (_logger.IsTrace) _logger.Trace($"Headers request {currentNumber}+{headersToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {headersToRequest} more.");
                var headers = await RequestHeaders(bestPeer, cancellation, currentNumber, headersToRequest);

                BlockHeader startingPoint = headers[0] == null ? null : _blockTree.FindHeader(headers[0].Hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (startingPoint == null)
                {
                    ancestorLookupLevel += _syncBatchSize.Current;
                    currentNumber = currentNumber >= _syncBatchSize.Current ? (currentNumber - _syncBatchSize.Current) : 0L;
                    continue;
                }

                _sinceLastTimeout++;
                if (_sinceLastTimeout >= 2)
                {
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
            }

            return headersSynced;
        }

        public async Task<long> DownloadBlocks(PeerInfo bestPeer, int newBlocksToSkip, CancellationToken cancellation, DownloadOptions options = DownloadOptions.DownloadAndProcess)
        {
            var downloadReceipts = options == DownloadOptions.DownloadWithReceipts;
            var shouldProcess = options == DownloadOptions.DownloadAndProcess;
            
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
                if (ancestorLookupLevel > MaxReorganizationLength)
                {
                    if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {bestPeer}");
                    throw new EthSynchronizationException("Peer with inconsistent chain in sync");
                }

                long blocksLeft = bestPeer.HeadNumber - currentNumber - newBlocksToSkip;
                int blocksToRequest = (int) BigInteger.Min(blocksLeft + 1, _syncBatchSize.Current);
                if (blocksToRequest <= 1)
                {
                    break;
                }

                if (_logger.IsTrace) _logger.Trace($"Full sync request {currentNumber}+{blocksToRequest} to peer {bestPeer} with {bestPeer.HeadNumber} blocks. Got {currentNumber} and asking for {blocksToRequest} more.");
                var headers = await RequestHeaders(bestPeer, cancellation, currentNumber, blocksToRequest);
                Block[] blocks = new Block[headers.Length - 1];

                List<Keccak> blockHashes = new List<Keccak>();
                Dictionary<int, int> indexMapping = new Dictionary<int, int>();
                int currentBodyIndex = 0;
                for (int i = 1; i < headers.Length; i++)
                {
                    if (headers[i] == null)
                    {
                        break;
                    }

                    if (headers[i].HasBody)
                    {
                        blocks[i - 1] = new Block(headers[i], (BlockBody) null);
                        indexMapping.Add(currentBodyIndex, i - 1);
                        currentBodyIndex++;
                        blockHashes.Add(headers[i].Hash);
                    }
                    else
                    {
                        blocks[i - 1] = new Block(headers[i], BlockBody.Empty);
                    }
                }

                Task<BlockBody[]> bodiesTask = blockHashes.Count == 0
                    ? Task.FromResult(Array.Empty<BlockBody>())
                    : bestPeer.SyncPeer.GetBlocks(blockHashes, cancellation);
                
                Task<TxReceipt[][]> receiptsTasks = !downloadReceipts || blockHashes.Count == 0
                    ? Task.FromResult(Array.Empty<TxReceipt[]>())
                    : bestPeer.SyncPeer.GetReceipts(blockHashes, cancellation);

                ValueTask DownloadFailHandler<T>(Task<T> t, string entities)
                {
                    if (t.IsFaulted)
                    {
                        _sinceLastTimeout = 0;
                        if (t.Exception?.InnerException is TimeoutException
                            || (t.Exception?.InnerExceptions.Any(x => x is TimeoutException) ?? false)
                            || (t.Exception?.InnerExceptions.Any(x => x.InnerException is TimeoutException) ?? false))
                        {
                            if (_logger.IsTrace) _logger.Error($"Failed to retrieve {entities} when synchronizing (Timeout)", bodiesTask.Exception);
                            _syncBatchSize.Shrink();
                        }
                        else
                        {
                            if (_logger.IsError) _logger.Error($"Failed to retrieve {entities} when synchronizing.", bodiesTask.Exception);
                        }

                        throw new EthSynchronizationException($"{entities} task faulted", t.Exception);
                    }

                    return default;
                }

                await Task.WhenAll(
                    bodiesTask.ContinueWith(t => DownloadFailHandler<BlockBody[]>(t, "bodies"), cancellation),
                    receiptsTasks.ContinueWith(t => DownloadFailHandler<TxReceipt[][]>(t, "receipts"), cancellation));
                

                if (bodiesTask.IsCanceled || receiptsTasks.IsCanceled)
                {
                    return blocksSynced;
                }

                TxReceipt[][] receipts = null;
                IDictionary<Keccak, IList<TxReceipt>> correctReceiptsBlocks = null;
                if (downloadReceipts)
                {
                    receipts = receiptsTasks.Result;
                    correctReceiptsBlocks = new Dictionary<Keccak, IList<TxReceipt>>();
                }
                
                BlockBody[] bodies = bodiesTask.Result;
                for (int i = 0; i < bodies.Length; i++)
                {
                    var body = bodies[i];
                    var block = blocks[indexMapping[i]];
                    
                    if (body == null)
                    {
                        // TODO: this is how it used to be... I do not want to touch it without extensive testing 
                        throw new EthSynchronizationException($"{bestPeer} sent an empty body for {block.ToString(Block.Format.Short)}.");
                    }
                    
                    TxReceipt[] blockReceipts = Array.Empty<TxReceipt>();
                    if (downloadReceipts)
                    {
                        if (receipts.Length > i)
                        {
                            blockReceipts = receipts[i] ?? Array.Empty<TxReceipt>();
                        }
                        else
                        {
                            throw new InvalidOperationException($"Failed to download receipts for block {block.ToString(Block.Format.Short)}.");
                        }
                    }
                    
                    ProcessDownloadedBody(body, block, downloadReceipts, blockReceipts, correctReceiptsBlocks);
                }

                _sinceLastTimeout++;
                if (_sinceLastTimeout > 2)
                {
                    _syncBatchSize.Expand();
                }

                int fullDataBlocksCount = 0;
                for (int i = 0; i < blocks.Length; i++)
                {
                    var block = blocks[i];
                    if (block?.Body != null)
                    {
                        IList<TxReceipt> receiptsForBlock = null;
                        if (!downloadReceipts || block.Body.Transactions.Length == 0 || correctReceiptsBlocks?.TryGetValue(block.Hash, out receiptsForBlock) == true)
                        {
                            if (receiptsForBlock != null)
                            {
                                for (int j = 0; j < receiptsForBlock.Count; j++)
                                {
                                    _receiptStorage.Add(receiptsForBlock[j], true);
                                }
                            }
                            
                            fullDataBlocksCount++;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                if (fullDataBlocksCount > 0)
                {
                    bool parentIsKnown = _blockTree.IsKnownBlock(blocks[0].Number - 1, blocks[0].ParentHash);
                    if (!parentIsKnown)
                    {
                        ancestorLookupLevel += _syncBatchSize.Current;
                        currentNumber = currentNumber >= _syncBatchSize.Current ? (currentNumber - _syncBatchSize.Current) : 0L;
                        continue;
                    }
                }

                for (int i = 0; i < fullDataBlocksCount; i++)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
                        break;
                    }

                    if (_logger.IsTrace) _logger.Trace($"Received {blocks[i]} from {bestPeer}");

                    // can move this to block tree now?
                    if (!_blockValidator.ValidateSuggestedBlock(blocks[i]))
                    {
                        throw new EthSynchronizationException($"{bestPeer} sent an invalid block {blocks[i].ToString(Block.Format.Short)}.");
                    }

                    if (HandleAddResult(blocks[i].Header, i == 0, _blockTree.SuggestBlock(blocks[i], shouldProcess)))
                    {
                        blocksSynced++;
                    }

                    currentNumber = currentNumber + 1;
                }

                if (blocksSynced > 0)
                {
                    _syncReport.FullSyncBlocksDownloaded.Update(_blockTree.BestSuggestedHeader?.Number ?? 0);
                    _syncReport.FullSyncBlocksKnown = bestPeer.HeadNumber;
                }
            }

            return blocksSynced;
        }

        private void ProcessDownloadedBody(
            BlockBody body,
            Block block, 
            bool downloadReceipts,
            TxReceipt[] blockReceipts,
            IDictionary<Keccak, IList<TxReceipt>> correctReceiptsBlocks)
        {
            if (blockReceipts == null) throw new ArgumentNullException(nameof(blockReceipts));
            if (block == null || block.Body != null)
            {
                throw new InvalidOperationException("Invalid state of blocks placeholders during sync");
            }

            block.Body = body;
            
            if (downloadReceipts)
            {
                if (body.Transactions.Length == blockReceipts.Length)
                {
                    List<TxReceipt> receiptsForBlock = new List<TxReceipt>();
                    correctReceiptsBlocks[block.Hash] = receiptsForBlock;
                    long gasUsedBefore = 0;

                    for (int j = 0; j < body.Transactions.Length; j++)
                    {
                        Transaction transaction = body.Transactions[j];
                        if (blockReceipts.Length > j)
                        {
                            TxReceipt receipt = blockReceipts[j];
                            RecoverReceiptData(receipt, block, transaction, j, gasUsedBefore);
                            receiptsForBlock.Add(receipt);
                            gasUsedBefore = receipt.GasUsedTotal;
                        }
                    }

                    ValidateReceipts(block, blockReceipts);
                }
                else
                {
                    throw new EthSynchronizationException($"Missing receipts for block {block.ToString(Block.Format.Short)}.");
                }
            }
        }

        private void ValidateReceipts(Block block, TxReceipt[] blockReceipts)
        {
            Keccak receiptsRoot = block.CalculateReceiptRoot(_specProvider, blockReceipts);
            if (receiptsRoot != block.ReceiptsRoot)
            {
                throw new EthSynchronizationException($"Wrong receipts root for downloaded block {block.ToString(Block.Format.Short)}.");
            }            
        }

        private static void RecoverReceiptData(TxReceipt receipt, Block block, Transaction transaction, int transactionIndex, long gasUsedBefore)
        {
            receipt.BlockHash = block.Hash;
            receipt.BlockNumber = block.Number;
            receipt.TxHash = transaction.Hash;
            receipt.Index = transactionIndex;
            receipt.Sender = transaction.SenderAddress;
            receipt.Recipient = transaction.IsContractCreation ? null : transaction.To;
            receipt.ContractAddress = transaction.IsContractCreation ? transaction.To : null;
            receipt.GasUsed = receipt.GasUsedTotal - gasUsedBefore;
            if (receipt.StatusCode != StatusCode.Success)
            {
                receipt.StatusCode = receipt.Logs.Length == 0 ? StatusCode.Failure : StatusCode.Success;
            }
        }

        private async Task<BlockHeader[]> RequestHeaders(PeerInfo bestPeer, CancellationToken cancellation, long currentNumber, int headersToRequest)
        {
            var headersRequest = bestPeer.SyncPeer.GetBlockHeaders(currentNumber, headersToRequest, 0, cancellation);
            await headersRequest.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _sinceLastTimeout = 0;
                    if (t.Exception?.InnerException is TimeoutException
                        || (t.Exception?.InnerExceptions.Any(x => x is TimeoutException) ?? false)
                        || (t.Exception?.InnerExceptions.Any(x => x.InnerException is TimeoutException) ?? false))
                    {
                        _syncBatchSize.Shrink();
                        if (_logger.IsTrace) _logger.Error("Failed to retrieve headers when synchronizing (Timeout)", t.Exception);
                    }
                    else
                    {
                        if (_logger.IsError) _logger.Error("Failed to retrieve headers when synchronizing", t.Exception);
                    }

                    throw new EthSynchronizationException("Headers task faulted.", t.Exception);
                }
            }, cancellation);

            cancellation.ThrowIfCancellationRequested();

            var headers = headersRequest.Result;
            ValidateSeals(cancellation, headers);
            ValidateBatchConsistency(bestPeer, headers);
            return headers;
        }

        private void ValidateBatchConsistency(PeerInfo bestPeer, BlockHeader[] headers)
        {
            // Parity 1.11 non canonical blocks when testing on 27/06
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
            var exceptions = new ConcurrentQueue<Exception>();
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