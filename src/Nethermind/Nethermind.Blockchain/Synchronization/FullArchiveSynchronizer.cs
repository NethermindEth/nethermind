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
using System.Data;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Mining;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization
{
    public class FullArchiveSynchronizer : IFullArchiveSynchronizer
    {
        private SynchronizationMode _mode = SynchronizationMode.Blocks;
        public const int MaxReorganizationLength = 2 * BlockDownloader.MaxBatchSize;
        private int _sinceLastTimeout;
        private UInt256 _lastSyncNumber = UInt256.Zero;

        private readonly ILogger _logger;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly IPerfService _perfService;
        private readonly IBlockDownloader _blockDownloader;

        private readonly ITransactionValidator _transactionValidator;
        private readonly Blockchain.ISyncConfig _syncConfig;
        private readonly IBlockTree _blockTree;

        private PeerInfo _currentSyncingPeerInfo;
        

        
        private bool _requestedSyncCancelDueToBetterPeer;
        
        private int _lastSyncPeersCount;

        

        public FullArchiveSynchronizer(
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ITransactionValidator transactionValidator,
            ILogManager logManager,
            Blockchain.ISyncConfig syncConfig,
            IPerfService perfService,
            IBlockDownloader blockDownloader)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _perfService = perfService ?? throw new ArgumentNullException(nameof(perfService));
            _blockDownloader = blockDownloader ?? throw new ArgumentNullException(nameof(blockDownloader));

            _transactionValidator = transactionValidator ?? throw new ArgumentNullException(nameof(transactionValidator));

            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));

            if (_logger.IsDebug && Head != null) _logger.Debug($"Initialized SynchronizationManager with head block {Head.ToString(BlockHeader.Format.Short)}");
        }
        
        public event EventHandler<SyncEventArgs> SyncEvent;

        private DateTime _lastFullInfo = DateTime.Now;

        [Todo(Improve.Readability, "Let us review the cancellation approach here")]
        private async Task SynchronizeWithPeerAsync(PeerInfo peerInfo)
        {
            if (_mode == SynchronizationMode.Blocks && _blockTree.BestKnownNumber == peerInfo.HeadNumber)
            {
                _mode = SynchronizationMode.NodeData;
                return;
            }
            
            if (_logger.IsDebug) _logger.Debug($"Starting sync process with {peerInfo} - theirs {peerInfo.HeadNumber} {peerInfo.TotalDifficulty} | ours {_blockTree.BestSuggested.Number} {_blockTree.BestSuggested.TotalDifficulty}");
            bool wasCanceled = false;

            ISynchronizationPeer peer = peerInfo.SyncPeer;

            const int maxLookup = MaxReorganizationLength;
            int ancestorLookupLevel = 0;
            int emptyBlockListCounter = 0;

            UInt256 currentNumber = UInt256.Min(_blockTree.BestKnownNumber, peerInfo.HeadNumber - 1);
            while (peerInfo.TotalDifficulty > (_blockTree.BestSuggested?.TotalDifficulty ?? 0) && currentNumber <= peerInfo.HeadNumber)
            {
                
                if (_logger.IsTrace) _logger.Trace($"Continue syncing with {peerInfo} (our best {_blockTree.BestKnownNumber})");

                if (ancestorLookupLevel > maxLookup)
                {
                    if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {peerInfo}");
                    throw new EthSynchronizationException("Peer with inconsistent chain in sync");
                }

                if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"Sync with {peerInfo} cancelled");
                    return;
                }

                UInt256 blocksLeft = peerInfo.HeadNumber - currentNumber;
                int blocksToRequest = (int) BigInteger.Min(blocksLeft + 1, _currentBatchSize);
                if (_logger.IsTrace) _logger.Trace($"Sync request {currentNumber}+{blocksToRequest} to peer {peerInfo.SyncPeer.Node.Id} with {peerInfo.HeadNumber} blocks. Got {currentNumber} and asking for {blocksToRequest} more.");

                Task<BlockHeader[]> headersTask = peer.GetBlockHeaders(currentNumber, blocksToRequest, 0, _peerSyncCancellationTokenSource.Token);
                BlockHeader[] headers = await headersTask;
                if (headersTask.IsCanceled)
                {
                    if (_logger.IsTrace) _logger.Trace("Headers task cancelled");
                    wasCanceled = true;
                    break;
                }

                if (headersTask.IsFaulted)
                {
                    _sinceLastTimeout = 0;
                    if (headersTask.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                    {
                        DecreaseBatchSize();
                        if (_logger.IsTrace) _logger.Error("Failed to retrieve headers when synchronizing (Timeout)", headersTask.Exception);
                    }
                    else
                    {
                        if (_logger.IsError) _logger.Error("Failed to retrieve headers when synchronizing", headersTask.Exception);
                    }

                    throw headersTask.Exception;
                }

                if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                {
                    if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
                    return;
                }

                List<Keccak> hashes = new List<Keccak>();
                Dictionary<Keccak, BlockHeader> headersByHash = new Dictionary<Keccak, BlockHeader>();
                for (int i = 1; i < headers.Length; i++)
                {
                    if (headers[i] == null)
                    {
                        break;
                    }

                    hashes.Add(headers[i].Hash);
                    headersByHash[headers[i].Hash] = headers[i];
                }
 
                if (hashes.Count == 0)
                {
                    if (headers.Length == 1)
                    {
                        // for some reasons we take current number as peerInfo.HeadNumber - 1 (I do not remember why)
                        // and also there may be a race in total difficulty measurement
                        return;
                    }
                    
                    throw new EthSynchronizationException("Peer sent an empty header list");
                }

                Task<Block[]> bodiesTask = peer.GetBlocks(hashes.ToArray(), _peerSyncCancellationTokenSource.Token);
                Block[] blocks = await bodiesTask;
                if (bodiesTask.IsCanceled)
                {
                    wasCanceled = true;
                    if (_logger.IsTrace) _logger.Trace("Bodies task cancelled");
                    break;
                }

                if (bodiesTask.IsFaulted)
                {
                    _sinceLastTimeout = 0;
                    if (bodiesTask.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                    {
                        if (_logger.IsTrace) _logger.Error("Failed to retrieve bodies when synchronizing (Timeout)", bodiesTask.Exception);
                    }
                    else
                    {
                        if (_logger.IsError) _logger.Error("Failed to retrieve bodies when synchronizing", bodiesTask.Exception);
                    }

                    throw bodiesTask.Exception;
                }

                if (blocks.Length == 0 && blocksLeft == 1)
                {
                    if (_logger.IsDebug) _logger.Debug($"{peerInfo} does not have block body for {hashes[0]}");
                }

                if (blocks.Length == 0 && ++emptyBlockListCounter >= 10)
                {
                    if (_currentBatchSize == MinBatchSize)
                    {
                        if (_logger.IsInfo) _logger.Info($"Received no blocks from {_currentSyncingPeerInfo} in response to {blocksToRequest} blocks requested. Cancelling.");
                        throw new EthSynchronizationException("Peer sent an empty block list");
                    }

                    if (_logger.IsInfo) _logger.Info($"Received no blocks from {_currentSyncingPeerInfo} in response to {blocksToRequest} blocks requested. Decreasing batch size from {_currentBatchSize}.");
                    DecreaseBatchSize();
                    continue;
                }

                if (blocks.Length != 0)
                {
                    if (_logger.IsTrace) _logger.Trace($"Blocks length is {blocks.Length}, counter is {emptyBlockListCounter}");
                    emptyBlockListCounter = 0;
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Blocks length is 0, counter is {emptyBlockListCounter}");
                    continue;
                }

                _sinceLastTimeout++;
                if (_sinceLastTimeout > 8)
                {
                    IncreaseBatchSize();
                }

                for (int i = 0; i < blocks.Length; i++)
                {
                    if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                    {
                        return;
                    }

                    blocks[i].Header = headersByHash[hashes[i]];
                }

                if (blocks.Length > 0)
                {
                    Block parent = _blockTree.FindParent(blocks[0]);
                    if (parent == null)
                    {
                        ancestorLookupLevel += _currentBatchSize;
                        currentNumber = currentNumber >= _currentBatchSize ? (currentNumber - (UInt256) _currentBatchSize) : UInt256.Zero;
                        continue;
                    }
                }

                /* // fast sync receipts download when ETH63 implemented fully
                if (await DownloadReceipts(blocks, peer)) break; */

                // Parity 1.11 non canonical blocks when testing on 27/06
                for (int i = 0; i < blocks.Length; i++)
                {
                    if (i != 0 && blocks[i].ParentHash != blocks[i - 1].Hash)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Inconsistent block list from peer {peerInfo}");
                        throw new EthSynchronizationException("Peer sent an inconsistent block list");
                    }
                }

                var exceptions = new ConcurrentQueue<Exception>();
                Parallel.For(0, blocks.Length, (i, state) =>
                {
                    if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        if (!_sealValidator.ValidateSeal(blocks[i].Header))
                        {
                            state.Stop();
                            throw new EthSynchronizationException("Peer sent a block with an invalid seal");
                        }
                    }
                    catch (Exception e)
                    {
                        exceptions.Enqueue(e);
                    }
                });

                if (exceptions.Count > 0)
                {
                    throw new AggregateException(exceptions);
                }

                for (int i = 0; i < blocks.Length; i++)
                {
                    if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
                        return;
                    }

                    if (_logger.IsTrace) _logger.Trace($"Received {blocks[i]} from {peer.Node:s}");

                    if (!_blockValidator.ValidateSuggestedBlock(blocks[i]))
                    {
                        if (_logger.IsWarn) _logger.Warn($"Block {blocks[i].Number} skipped (validation failed)");
                        continue;
                    }

                    AddBlockResult addResult = _blockTree.SuggestBlock(blocks[i]);
                    switch (addResult)
                    {
                        case AddBlockResult.UnknownParent:
                        {
                            if (_logger.IsTrace)
                                _logger.Trace($"Block {blocks[i].Number} ignored (unknown parent)");
                            if (i == 0)
                            {
                                const string message = "Peer sent orphaned blocks inside the batch";
                                _logger.Error(message);
                                throw new EthSynchronizationException(message);
                            }
                            else
                            {
                                const string message = "Peer sent an inconsistent batch of block headers";
                                _logger.Error(message);
                                throw new EthSynchronizationException(message);
                            }
                        }
                        case AddBlockResult.CannotAccept:
                            return;
                        case AddBlockResult.InvalidBlock:
                            throw new EthSynchronizationException("Peer sent an invalid block");
                        case AddBlockResult.Added:
                            if (_logger.IsTrace) _logger.Trace($"Block {blocks[i].Number} suggested for processing");
                            continue;
                        case AddBlockResult.AlreadyKnown:
                            if (_logger.IsTrace) _logger.Trace($"Block {blocks[i].Number} skipped - already known");
                            continue;
                    }
                }

                currentNumber = blocks[blocks.Length - 1].Number;
                if (_blockTree.BestKnownNumber > _lastSyncNumber + 10000 || _blockTree.BestKnownNumber < _lastSyncNumber)
                {
                    _lastSyncNumber = _blockTree.BestKnownNumber;
                    if (_logger.IsDebug) _logger.Debug($"Downloading blocks. Current best at {_blockTree.BestSuggested?.ToString(BlockHeader.Format.Short)}");
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Stopping sync processes with {peerInfo}, wasCancelled: {wasCanceled}");
        }
    }
}