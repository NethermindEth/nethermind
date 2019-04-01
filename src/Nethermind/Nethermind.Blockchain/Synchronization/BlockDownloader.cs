///*
// * Copyright (c) 2018 Demerzel Solutions Limited
// * This file is part of the Nethermind library.
// *
// * The Nethermind library is free software: you can redistribute it and/or modify
// * it under the terms of the GNU Lesser General Public License as published by
// * the Free Software Foundation, either version 3 of the License, or
// * (at your option) any later version.
// *
// * The Nethermind library is distributed in the hope that it will be useful,
// * but WITHOUT ANY WARRANTY; without even the implied warranty of
// * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// * GNU Lesser General Public License for more details.
// *
// * You should have received a copy of the GNU Lesser General Public License
// * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// */
//
//using System;
//using System.Collections.Concurrent;
//using System.Collections.Generic;
//using System.Linq;
//using System.Numerics;
//using System.Threading;
//using System.Threading.Tasks;
//using Nethermind.Core;
//using Nethermind.Core.Crypto;
//using Nethermind.Core.Logging;
//using Nethermind.Dirichlet.Numerics;
//
//namespace Nethermind.Blockchain.Synchronization
//{
//    public class BlockDownloader : IBlockDownloader
//    {
//
//        private readonly IEthSyncPeerPool _pool;
//        private readonly ILogger _logger;
//
//        public BlockDownloader(IEthSyncPeerPool peerPool, ILogManager logManager)
//        {
//            _pool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
//            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
//        }
//
//        private SyncPeerAllocation _allocation;
//        
//        
//        public void Start()
//        {
//            // check if peer pool is started here
//            _allocation = _pool.BorrowPeer(0);
//            _allocation.Replaced += AllocationOnReplaced;
//            _allocation.Cancelled += AllocationOnCancelled;
//        }
//
//        private void AllocationOnCancelled(object sender, AllocationChangeEventArgs e)
//        {
//            throw new NotImplementedException();
//        }
//
//        private void  AllocationOnReplaced(object sender, AllocationChangeEventArgs e)
//        {
//            throw new NotImplementedException();
//        }
//
//        public Task StopAsync()
//        {
//            _allocation.Replaced -= AllocationOnReplaced;
//            _allocation.Cancelled -= AllocationOnCancelled;
//            return Task.CompletedTask;
//        }
//
//        private CancellationTokenSource _currentDownload;
//        
//        public Task<Block[]> DownloadBlocks(UInt256 bestKnownNumber, BlockHeader bestSuggested)
//        {
//            PeerInfo peerInfo = _allocation.Current;
//            ISyncPeer peer = peerInfo.SyncPeer;
//
//            const int maxLookup = MaxReorganizationLength;
//            int ancestorLookupLevel = 0;
//            int emptyBlockListCounter = 0;
//            
//            UInt256 currentNumber = UInt256.Min(bestKnownNumber, peerInfo.HeadNumber - 1);
//            while (peerInfo.TotalDifficulty > (bestSuggested?.TotalDifficulty ?? 0) && currentNumber <= peerInfo.HeadNumber)
//            {
//                _currentDownload = new CancellationTokenSource();
//                
//                if (_logger.IsTrace) _logger.Trace($"Continue syncing with {peerInfo} (our best {bestKnownNumber})");
//
//                if (ancestorLookupLevel > maxLookup)
//                {
//                    if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {peerInfo}");
//                    throw new EthSynchronizationException("Peer with inconsistent chain in sync");
//                }
//
//                if (_currentDownload.IsCancellationRequested)
//                {
//                    if (_logger.IsInfo) _logger.Info($"Block download from {peerInfo} cancelled");
//                    continue;
//                }
//
//                UInt256 blocksLeft = peerInfo.HeadNumber - currentNumber;
//                int blocksToRequest = (int) BigInteger.Min(blocksLeft + 1, _currentBatchSize);
//                if (_logger.IsTrace) _logger.Trace($"Sync request {currentNumber}+{blocksToRequest} to peer {peerInfo.SyncPeer.Node.Id} with {peerInfo.HeadNumber} blocks. Got {currentNumber} and asking for {blocksToRequest} more.");
//
//                Task<BlockHeader[]> headersTask = peer.GetBlockHeaders(currentNumber, blocksToRequest, 0, _peerSyncCancellationTokenSource.Token);
//                BlockHeader[] headers = await headersTask;
//                if (headersTask.IsCanceled)
//                {
//                    if (_logger.IsTrace) _logger.Trace("Headers task cancelled");
//                    wasCanceled = true;
//                    break;
//                }
//
//                if (headersTask.IsFaulted)
//                {
//                    _sinceLastTimeout = 0;
//                    if (headersTask.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
//                    {
//                        DecreaseBatchSize();
//                        if (_logger.IsTrace) _logger.Error("Failed to retrieve headers when synchronizing (Timeout)", headersTask.Exception);
//                    }
//                    else
//                    {
//                        if (_logger.IsError) _logger.Error("Failed to retrieve headers when synchronizing", headersTask.Exception);
//                    }
//
//                    throw headersTask.Exception;
//                }
//
//                if (_peerSyncCancellationTokenSource.IsCancellationRequested)
//                {
//                    if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
//                    return;
//                }
//
//                List<Keccak> hashes = new List<Keccak>();
//                Dictionary<Keccak, BlockHeader> headersByHash = new Dictionary<Keccak, BlockHeader>();
//                for (int i = 1; i < headers.Length; i++)
//                {
//                    if (headers[i] == null)
//                    {
//                        break;
//                    }
//
//                    hashes.Add(headers[i].Hash);
//                    headersByHash[headers[i].Hash] = headers[i];
//                }
// 
//                if (hashes.Count == 0)
//                {
//                    if (headers.Length == 1)
//                    {
//                        // for some reasons we take current number as peerInfo.HeadNumber - 1 (I do not remember why)
//                        // and also there may be a race in total difficulty measurement
//                        return;
//                    }
//                    
//                    throw new EthSynchronizationException("Peer sent an empty header list");
//                }
//
//                Task<Block[]> bodiesTask = peer.GetBlocks(hashes.ToArray(), _peerSyncCancellationTokenSource.Token);
//                Block[] blocks = await bodiesTask;
//                if (bodiesTask.IsCanceled)
//                {
//                    wasCanceled = true;
//                    if (_logger.IsTrace) _logger.Trace("Bodies task cancelled");
//                    break;
//                }
//
//                if (bodiesTask.IsFaulted)
//                {
//                    _sinceLastTimeout = 0;
//                    if (bodiesTask.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
//                    {
//                        if (_logger.IsTrace) _logger.Error("Failed to retrieve bodies when synchronizing (Timeout)", bodiesTask.Exception);
//                    }
//                    else
//                    {
//                        if (_logger.IsError) _logger.Error("Failed to retrieve bodies when synchronizing", bodiesTask.Exception);
//                    }
//
//                    throw bodiesTask.Exception;
//                }
//
//                if (blocks.Length == 0 && blocksLeft == 1)
//                {
//                    if (_logger.IsDebug) _logger.Debug($"{peerInfo} does not have block body for {hashes[0]}");
//                }
//
//                if (blocks.Length == 0 && ++emptyBlockListCounter >= 10)
//                {
//                    if (_currentBatchSize == MinBatchSize)
//                    {
//                        if (_logger.IsInfo) _logger.Info($"Received no blocks from {_currentSyncingPeerInfo} in response to {blocksToRequest} blocks requested. Cancelling.");
//                        throw new EthSynchronizationException("Peer sent an empty block list");
//                    }
//
//                    if (_logger.IsInfo) _logger.Info($"Received no blocks from {_currentSyncingPeerInfo} in response to {blocksToRequest} blocks requested. Decreasing batch size from {_currentBatchSize}.");
//                    DecreaseBatchSize();
//                    continue;
//                }
//
//                if (blocks.Length != 0)
//                {
//                    if (_logger.IsTrace) _logger.Trace($"Blocks length is {blocks.Length}, counter is {emptyBlockListCounter}");
//                    emptyBlockListCounter = 0;
//                }
//                else
//                {
//                    if (_logger.IsTrace) _logger.Trace($"Blocks length is 0, counter is {emptyBlockListCounter}");
//                    continue;
//                }
//
//                _sinceLastTimeout++;
//                if (_sinceLastTimeout > 8)
//                {
//                    IncreaseBatchSize();
//                }
//
//                for (int i = 0; i < blocks.Length; i++)
//                {
//                    if (_peerSyncCancellationTokenSource.IsCancellationRequested)
//                    {
//                        return;
//                    }
//
//                    blocks[i].Header = headersByHash[hashes[i]];
//                }
//
//                if (blocks.Length > 0)
//                {
//                    Block parent = _blockTree.FindParent(blocks[0]);
//                    if (parent == null)
//                    {
//                        ancestorLookupLevel += _currentBatchSize;
//                        currentNumber = currentNumber >= _currentBatchSize ? (currentNumber - (UInt256) _currentBatchSize) : UInt256.Zero;
//                        continue;
//                    }
//                }
//
//                /* // fast sync receipts download when ETH63 implemented fully
//                if (await DownloadReceipts(blocks, peer)) break; */
//
//                // Parity 1.11 non canonical blocks when testing on 27/06
//                for (int i = 0; i < blocks.Length; i++)
//                {
//                    if (i != 0 && blocks[i].ParentHash != blocks[i - 1].Hash)
//                    {
//                        if (_logger.IsTrace) _logger.Trace($"Inconsistent block list from peer {peerInfo}");
//                        throw new EthSynchronizationException("Peer sent an inconsistent block list");
//                    }
//                }
//
//                var exceptions = new ConcurrentQueue<Exception>();
//                Parallel.For(0, blocks.Length, (i, state) =>
//                {
//                    if (_peerSyncCancellationTokenSource.IsCancellationRequested)
//                    {
//                        return;
//                    }
//
//                    try
//                    {
//                        if (!_sealValidator.ValidateSeal(blocks[i].Header))
//                        {
//                            state.Stop();
//                            throw new EthSynchronizationException("Peer sent a block with an invalid seal");
//                        }
//                    }
//                    catch (Exception e)
//                    {
//                        exceptions.Enqueue(e);
//                    }
//                });
//
//                if (exceptions.Count > 0)
//                {
//                    throw new AggregateException(exceptions);
//                }
//            }
//            
//            if (_logger.IsTrace) _logger.Trace($"Stopping sync processes with {peerInfo}, wasCancelled: {wasCanceled}");
//        }
//    }
//}