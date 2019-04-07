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
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Mining;
using PublicKey = System.Security.Cryptography.X509Certificates.PublicKey;

namespace Nethermind.Blockchain.Synchronization
{
    public class FastSynchronizer : ISynchronizer, INodeDataRequestExecutor
    {
        private int _sinceLastTimeout;
        private long _lastSyncNumber = 0L;

        private readonly ILogger _logger;
        private readonly IHeaderValidator _headerValidator;
        private readonly ISealValidator _sealValidator;
        private readonly IEthSyncPeerPool _syncPeerPool;

        private readonly ITxValidator _txValidator;
        private readonly Blockchain.ISyncConfig _syncConfig;
        private readonly INodeDataDownloader _nodeDataDownloader;
        private readonly IBlockTree _blockTree;

        private int _currentBatchSize = 256;

        public const int MinBatchSize = 8;

        public const int MaxBatchSize = 512;

        public const int MaxReorganizationLength = 2 * MaxBatchSize;

        private void IncreaseBatchSize()
        {
            _currentBatchSize = Math.Min(MaxBatchSize, _currentBatchSize * 2);
            if (_logger.IsDebug) _logger.Debug($"Changing batch size to {_currentBatchSize}");
        }

        private void DecreaseBatchSize()
        {
            _currentBatchSize = Math.Max(MinBatchSize, _currentBatchSize / 2);
            if (_logger.IsDebug) _logger.Debug($"Changing batch size to {_currentBatchSize}");
        }

        private ISynchronizer _fullSynchronizer;
        
        public FastSynchronizer(IBlockTree blockTree,
            IHeaderValidator headerValidator,
            ISealValidator sealValidator,
            ITxValidator txValidator,
            IEthSyncPeerPool peerPool,
            ISyncConfig syncConfig,
            INodeDataDownloader nodeDataDownloader,
            ISynchronizer fullSynchronizer,
            ILogManager logManager)
        {
            _fullSynchronizer = fullSynchronizer ?? throw new ArgumentNullException(nameof(fullSynchronizer));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _nodeDataDownloader = nodeDataDownloader ?? throw new ArgumentNullException(nameof(nodeDataDownloader));
            _syncPeerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));

            _txValidator = txValidator ?? throw new ArgumentNullException(nameof(txValidator));

            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));

            _nodeDataDownloader.SetExecutor(this);

            _fullSynchronizer.SyncEvent += (s, e) => SyncEvent?.Invoke(this, e);
        }

        private CancellationTokenSource _syncLoopCancelTokenSource = new CancellationTokenSource();

        private Task _syncLoopTask;

        public async Task StopAsync()
        {
            await _fullSynchronizer.StopAsync();
            await StopFastSync();
        }

        private async Task StopFastSync()
        {
            StopSyncTimer();
        
            _peerSyncCancellationTokenSource?.Cancel();
            _syncLoopCancelTokenSource?.Cancel();

            await (_syncLoopTask ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Sync shutdown complete.. please wait for all components to close");
        }

        private System.Timers.Timer _syncTimer;

        private void StopSyncTimer()
        {
            try
            {
                if (_logger.IsDebug) _logger.Debug("Stopping sync timer");
                _syncTimer?.Stop();
            }
            catch (Exception e)
            {
                _logger.Error("Error during sync timer stop", e);
            }
        }

        public void Start()
        {
//            _syncLoopTask = Task.Run(RunSyncLoop, _syncLoopCancelTokenSource.Token) 
            _syncLoopTask = Task.Factory.StartNew(
                    RunSyncLoop,
                    _syncLoopCancelTokenSource.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap()
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error("Sync loop encountered an exception.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        if (_logger.IsDebug) _logger.Debug("Sync loop stopped.");
                    }
                    else if (t.IsCompleted)
                    {
                        if (_logger.IsDebug) _logger.Debug("Sync loop complete.");
                    }
                });

            StartSyncTimer();
        }

        private DateTime _lastFullInfo = DateTime.UtcNow;
        private int _lastSyncPeersCount;

        private void StartSyncTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting sync timer");
            _syncTimer = new System.Timers.Timer(_syncConfig.SyncTimerInterval);
            _syncTimer.Elapsed += (s, e) =>
            {
                try
                {
                    _syncTimer.Enabled = false;
                    var initPeerCount = _syncPeerPool.AllPeers.Count(p => p.IsInitialized);

                    if (DateTime.UtcNow - _lastFullInfo > TimeSpan.FromSeconds(120) && _logger.IsDebug)
                    {
                        if (_logger.IsDebug) _logger.Debug("Sync peers:");
                        foreach (PeerInfo peerInfo in _syncPeerPool.AllPeers)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{peerInfo}");
                        }

                        _lastFullInfo = DateTime.UtcNow;
                    }
                    else if (initPeerCount != _lastSyncPeersCount)
                    {
                        _lastSyncPeersCount = initPeerCount;
                        if (_logger.IsInfo) _logger.Info($"Sync peers {initPeerCount}({_syncPeerPool.PeerCount})/{_syncConfig.SyncPeersMaxCount} {(_allocation.Current != null ? $"(sync in progress with {_allocation.Current})" : string.Empty)}");
                    }
                    else if (initPeerCount == 0)
                    {
                        if (_logger.IsInfo) _logger.Info($"Sync peers 0, searching for peers to sync with...");
                    }

                    if (_logger.IsTrace) _logger.Trace("Requesting synchronization");
                    _syncRequested.Set();
                }
                catch (Exception exception)
                {
                    if (_logger.IsDebug) _logger.Error("Sync timer failed", exception);
                }
                finally
                {
                    _syncTimer.Enabled = true;
                }
            };

            _syncTimer.Start();
        }

        private bool _requestedSyncCancelDueToBetterPeer;

        private CancellationTokenSource _peerSyncCancellationTokenSource;

        public event EventHandler<SyncEventArgs> SyncEvent;

        public void RequestSynchronization(string reason)
        {
            if (_mode == SynchronizationMode.Full)
            {
                _fullSynchronizer.RequestSynchronization(reason);
                return;
            }
            
            if (_logger.IsTrace) _logger.Trace($"Requesting synchronization {reason}");
            _syncRequested.Set();
        }

        private readonly ManualResetEventSlim _syncRequested = new ManualResetEventSlim(false);

        private SyncPeerAllocation _allocation;

        private async Task RunSyncLoop()
        {
            if (_logger.IsDebug) _logger.Debug("Initializing sync loop.");
            _allocation = _syncPeerPool.BorrowPeer("fast sync");
            if (_logger.IsDebug) _logger.Debug("Sync loop allocated.");
            _allocation.Replaced += AllocationOnReplaced;
            _allocation.Cancelled += AllocationOnCancelled;

            while (true)
            {
                if (_logger.IsTrace) _logger.Trace("Sync loop - next iteration WAIT.");
                _syncRequested.Wait(_syncLoopCancelTokenSource.Token);
                _syncRequested.Reset();
                if (_logger.IsTrace) _logger.Trace("Sync loop - next iteration IN.");
                /* If block tree is processing blocks from DB then we are not going to start the sync process.
                 * In the future it may make sense to run sync anyway and let DB loader know that there are more blocks waiting.
                 * */

                if (!_blockTree.CanAcceptNewBlocks) continue;

                _syncPeerPool.EnsureBest(_allocation, _blockTree.BestSuggested?.TotalDifficulty ?? 0);
                if (_allocation.Current == null || _allocation.Current.TotalDifficulty <= (_blockTree.BestSuggested?.TotalDifficulty ?? 0))
                {
                    if (_logger.IsDebug) _logger.Debug("Skipping - no better peer to sync with.");
                    continue;
                }

                while (true)
                {
                    if (_syncLoopCancelTokenSource.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Sync loop cancellation requested - leaving.");
                        break;
                    }

                    var peerInfo = _allocation.Current;
                    if (peerInfo == null)
                    {
                        if (_logger.IsDebug)
                            _logger.Debug(
                                "No more peers with better block available, finishing sync process, " +
                                $"best known block #: {_blockTree.BestKnownNumber}, " +
                                $"best peer block #: {(_syncPeerPool.PeerCount != 0 ? _syncPeerPool.AllPeers.Max(x => x.HeadNumber) : 0)}");
                        break;
                    }

                    SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, SyncStatus.Started));

                    _peerSyncCancellationTokenSource = new CancellationTokenSource();
                    var peerSynchronizationTask = SynchronizeWithPeerAsync(peerInfo);
                    await peerSynchronizationTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                        {
                            if (_logger.IsDebug) // only reports this error when viewed in the Debug mode
                            {
                                if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x is TimeoutException))
                                {
                                    _logger.Debug($"Stopping sync with node: {peerInfo}. {t.Exception?.Message}");
                                }
                                else
                                {
                                    _logger.Error($"Stopping sync with node: {peerInfo}. Error in the sync process.", t.Exception);
                                }
                            }

                            _syncPeerPool.RemovePeer(peerInfo.SyncPeer);
                            if (_logger.IsTrace) _logger.Trace($"Sync with {peerInfo} failed. Removed node from sync peers.");
                            SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, SyncStatus.Failed));
                        }
                        else if (t.IsCanceled || _peerSyncCancellationTokenSource.IsCancellationRequested)
                        {
                            if (_requestedSyncCancelDueToBetterPeer)
                            {
                                _requestedSyncCancelDueToBetterPeer = false;
                            }
                            else
                            {
                                _syncPeerPool.RemovePeer(peerInfo.SyncPeer);
                                if (_logger.IsTrace) _logger.Trace($"Sync with {peerInfo} canceled. Removed node from sync peers.");
                                SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, SyncStatus.Cancelled));
                            }
                        }
                        else if (t.IsCompleted)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Sync process finished with {peerInfo}. Best known block is ({_blockTree.BestKnownNumber})");
                            SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, SyncStatus.Completed));
                        }

                        if (_logger.IsDebug)
                            _logger.Debug(
                                $"Finished peer sync process [{(t.IsFaulted ? "FAULTED" : t.IsCanceled ? "CANCELED" : t.IsCompleted ? "COMPLETED" : "OTHER")}] with {peerInfo}], " +
                                $"best known block #: {_blockTree.BestKnownNumber} ({_blockTree.BestKnownNumber}), " +
                                $"best peer block #: {peerInfo.HeadNumber} ({peerInfo.HeadNumber})");

                        _allocation.FinishSync();

                        var source = _peerSyncCancellationTokenSource;
                        _peerSyncCancellationTokenSource = null;
                        source?.Dispose();
                    }, _syncLoopCancelTokenSource.Token);
                }

                _allocation.FinishSync();
                if(_logger.IsInfo) _logger.Info($"[FAST SYNC] Current sync at {_blockTree.BestSuggested?.Number}!");
                if ((_blockTree.BestSuggested?.Number ?? 0) > 0) // make it 1024 * 128 or configurable for tests
                {
                    _syncPeerPool.EnsureBest(_allocation, (_blockTree.BestSuggested?.TotalDifficulty - 1) ?? 0);
                    if ((_allocation.Current?.HeadNumber ?? 0) <= (_blockTree.BestSuggested?.Number ?? 0) + 1024)
                    {
                        if(_logger.IsInfo) _logger.Info($"[FAST SYNC] Switching to node data download at block {_blockTree.BestSuggested?.Number}!");
                        foreach (PeerInfo peerInfo in _syncPeerPool.AllPeers)
                        {
                            if(_logger.IsInfo) _logger.Info($"[FAST SYNC] Peers:");
                            if(_logger.IsInfo) _logger.Info($"[FAST SYNC] {peerInfo}!");
                        }

                        List<Keccak> stateRoots = new List<Keccak>();
                        BlockHeader bestSuggested = _blockTree.BestSuggested;
                        if (bestSuggested == null)
                        {
                            if (_logger.IsError) _logger.Error("Best suggested block is null when starting fast sync!");
                            throw new EthSynchronizationException("Best suggested block is null when starting fast sync!");
                        }
                        
                        stateRoots.Add(bestSuggested.StateRoot);
//                        for (int i = 0; i < 64; i++)
//                        {
//                            stateRoots.Add(_blockTree.FindHeader(bestSuggested.ParentHash).StateRoot);
//                        }

                        _mode = SynchronizationMode.NodeData;
                        await _nodeDataDownloader.SyncNodeData(stateRoots.Select<Keccak, (Keccak, NodeDataType)>(sr => (sr, NodeDataType.State)).ToArray());
                        _mode = SynchronizationMode.Full;
                        
                        _allocation.Replaced -= AllocationOnReplaced;
                        _allocation.Cancelled -= AllocationOnCancelled;
                        _syncPeerPool.ReturnPeer(_allocation);

                        if(_logger.IsInfo) _logger.Info($"[FAST SYNC] complete");
                        
                        // avoid deadlocking here
#pragma warning disable 4014
                        StopFastSync().ContinueWith(t =>
#pragma warning restore 4014
                        {
                            _fullSynchronizer.Start();
                            _fullSynchronizer.RequestSynchronization("fast sync complete");    
                        });
                    }
                }
            }
        }

        private SynchronizationMode _mode = SynchronizationMode.Blocks;

        private void AllocationOnCancelled(object sender, AllocationChangeEventArgs e)
        {
            if (_logger.IsDebug) _logger.Debug($"Cancelling {e.Previous} on {_allocation}.");
            _peerSyncCancellationTokenSource?.Cancel();
        }

        private void AllocationOnReplaced(object sender, AllocationChangeEventArgs e)
        {
            if (e.Previous == null)
            {
                if (_logger.IsDebug) _logger.Debug($"Allocating {e.Current} to {_allocation}.");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Replacing {e.Previous} with {e.Current} on {_allocation}.");
            }

            if (e.Previous != null)
            {
                _requestedSyncCancelDueToBetterPeer = true;
                _peerSyncCancellationTokenSource?.Cancel();
            }

            PeerInfo newPeer = e.Current;
            if (newPeer.TotalDifficulty > _blockTree.BestSuggested.TotalDifficulty)
            {
                if (_logger.IsTrace) _logger.Trace("Requesting synchronization - REPLACE");
                _syncRequested.Set();
            }
        }

        [Todo(Improve.Readability, "Review cancellation")]
        private async Task SynchronizeWithPeerAsync(PeerInfo peerInfo)
        {
            if (_logger.IsDebug) _logger.Debug($"Starting sync process with {peerInfo} - theirs {peerInfo.HeadNumber} {peerInfo.TotalDifficulty} | ours {_blockTree.BestSuggested.Number} {_blockTree.BestSuggested.TotalDifficulty}");
            bool wasCanceled = false;

            ISyncPeer peer = peerInfo.SyncPeer;

            const int maxLookup = MaxReorganizationLength;
            int ancestorLookupLevel = 0;
            int emptyBlockListCounter = 0;

            // fast sync 64 (BetsKnown + 64 below) here - review where it should be added
            long currentNumber = Math.Min(_blockTree.BestKnownNumber + 64, peerInfo.HeadNumber - 1);
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

                long blocksLeft = peerInfo.HeadNumber - currentNumber;
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
                try
                {
                    for (int i = 1; i < headers.Length; i++)
                    {
                        BlockHeader header = headers[i];
                        if (header == null)
                        {
                            break;
                        }

                        hashes.Add(header.Hash);
                        headersByHash[header.Hash] = header;
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
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

                var hashesArray = hashes.ToArray();
                if (_logger.IsTrace) _logger.Trace($"Actual batch size was {hashesArray.Length}/{_currentBatchSize}");

                BlockHeader[] blockHeaders = new BlockHeader[hashesArray.Length];

                if (blockHeaders.Length == 0 && blocksLeft == 1)
                {
                    if (_logger.IsDebug) _logger.Debug($"{peerInfo} does not have block body for {hashes[0]}");
                }

                if (blockHeaders.Length == 0 && ++emptyBlockListCounter >= 10)
                {
                    if (_currentBatchSize == MinBatchSize)
                    {
                        if (_logger.IsInfo) _logger.Info($"Received no blocks from {_allocation.Current} in response to {blocksToRequest} blocks requested. Cancelling.");
                        throw new EthSynchronizationException("Peer sent an empty block list");
                    }

                    if (_logger.IsInfo) _logger.Info($"Received no blocks from {_allocation.Current} in response to {blocksToRequest} blocks requested. Decreasing batch size from {_currentBatchSize}.");
                    DecreaseBatchSize();
                    continue;
                }

                if (blockHeaders.Length != 0)
                {
                    if (_logger.IsTrace) _logger.Trace($"Blocks length is {blockHeaders.Length}, counter is {emptyBlockListCounter}");
                    emptyBlockListCounter = 0;
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Blocks length is 0, counter is {emptyBlockListCounter}");
                    continue;
                }

                _sinceLastTimeout++;
                if (_sinceLastTimeout > 2 && _currentBatchSize != MaxBatchSize)
                {
                    IncreaseBatchSize();
                }

                for (int i = 0; i < blockHeaders.Length; i++)
                {
                    if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Cancel requested - stopping sync with {peerInfo}");
                        return;
                    }

                    blockHeaders[i] = headersByHash[hashes[i]];
                }

                if (blockHeaders.Length > 0)
                {
                    BlockHeader parent = _blockTree.FindParentHeader(blockHeaders[0]);
                    if (parent == null)
                    {
                        ancestorLookupLevel += _currentBatchSize;
                        currentNumber = currentNumber >= _currentBatchSize ? (currentNumber - _currentBatchSize) : 0L;
                        continue;
                    }
                }

                /* // fast sync receipts download when ETH63 implemented fully
                if (await DownloadReceipts(blocks, peer)) break; */

                // Parity 1.11 non canonical blocks when testing on 27/06
                for (int i = 0; i < blockHeaders.Length; i++)
                {
                    if (i != 0 && blockHeaders[i].ParentHash != blockHeaders[i - 1].Hash)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Inconsistent block list from peer {peerInfo}");
                        throw new EthSynchronizationException($"Peer sent an inconsistent block list - {currentNumber + 1 + i}.ParentHash ({blockHeaders[i].ParentHash}) != {currentNumber + 1 + i - 1}.Hash ({blockHeaders[i - 1].Hash})");
                    }
                }

                if (_logger.IsTrace) _logger.Trace($"Starting seal validation");
                var exceptions = new ConcurrentQueue<Exception>();
                Parallel.For(0, blockHeaders.Length, (i, state) =>
                {
                    if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Returning fom seal validation");
                        return;
                    }

                    try
                    {
                        if (!_sealValidator.ValidateSeal(blockHeaders[i]))
                        {
                            if (_logger.IsTrace) _logger.Trace($"One of the seals is invalid");
                            state.Stop();
                            throw new EthSynchronizationException("Peer sent a block with an invalid seal");
                        }
                    }
                    catch (Exception e)
                    {
                        exceptions.Enqueue(e);
                    }
                });

                if (_logger.IsTrace) _logger.Trace($"Seal validation complete");

                if (exceptions.Count > 0)
                {
                    if (_logger.IsDebug) _logger.Debug($"Seal validation failure");
                    throw new AggregateException(exceptions);
                }

                for (int i = 0; i < blockHeaders.Length; i++)
                {
                    if (_peerSyncCancellationTokenSource.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Peer sync cancelled");
                        return;
                    }

                    if (_logger.IsTrace) _logger.Trace($"Received {blockHeaders[i]} from {peer.Node:s}");

                    if (!_headerValidator.Validate(blockHeaders[i]))
                    {
                        if (_logger.IsWarn) _logger.Warn($"Block {blockHeaders[i].Number} skipped (validation failed)");
                        continue;
                    }

                    AddBlockResult addResult = _blockTree.SuggestHeader(blockHeaders[i]);
                    switch (addResult)
                    {
                        case AddBlockResult.UnknownParent:
                        {
                            if (_logger.IsTrace)
                                _logger.Trace($"Block {blockHeaders[i].Number} ignored (unknown parent)");
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
                            if (_logger.IsTrace) _logger.Trace($"Block {blockHeaders[i].Number} suggested for processing");
                            continue;
                        case AddBlockResult.AlreadyKnown:
                            if (_logger.IsTrace) _logger.Trace($"Block {blockHeaders[i].Number} skipped - already known");
                            continue;
                    }
                }

                currentNumber = blockHeaders[blockHeaders.Length - 1].Number;
                if (_blockTree.BestKnownNumber > _lastSyncNumber + 1000 || _blockTree.BestKnownNumber < _lastSyncNumber)
                {
                    _lastSyncNumber = _blockTree.BestKnownNumber;
                    if (_logger.IsInfo) _logger.Info($"Downloading headers {_blockTree.BestSuggested?.Number}/{_allocation.Current?.HeadNumber}");
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Stopping sync processes with {peerInfo}, wasCancelled: {wasCanceled}");
        }

        public async Task<NodeDataRequest> ExecuteRequest(NodeDataRequest request)
        {
            ISyncPeer peer = _allocation.Current?.SyncPeer;
            if (peer == null)
            {
                return request;
            }

            var hashes = request.Request.Select(r => r.Hash).ToArray();
            request.Response =
                await peer.GetNodeData(hashes, _syncLoopCancelTokenSource.Token);
            return request;
        }
    }
}


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
//using System.Data;
//using System.Linq;
//using System.Threading;
//using System.Threading.Tasks;
//using Nethermind.Blockchain.Receipts;
//using Nethermind.Core;
//using Nethermind.Core.Logging;
//
//namespace Nethermind.Blockchain.Synchronization
//{
//    public class FastSynchronizer : IFastSynchronizer
//    {
//        private readonly ILogger _logger;
//        private readonly INodeDataDownloader _nodeDataDownloader;
//        private readonly IReceiptStorage _receiptStorage;
//
//        public FastSynchronizer(INodeDataDownloader nodeDataDownloader, IReceiptStorage receiptStorage, ILogManager logManager)
//        {
//            _nodeDataDownloader = nodeDataDownloader ?? throw new ArgumentNullException(nameof(nodeDataDownloader));
//            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
//            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
//        }
//
//        [Todo(Improve.MissingFunctionality, "Eth63 / fast sync can download receipts using this method. Fast sync is not implemented although its methods and serializers are already written.")]
//        private async Task<bool> DownloadReceipts(Block[] blocks, ISyncPeer peer)
//        {
//            var blocksWithTransactions = blocks.Where(b => b.Transactions.Length != 0).ToArray();
//            if (blocksWithTransactions.Length != 0)
//            {
//                var receiptsTask = peer.GetReceipts(blocksWithTransactions.Select(b => b.Hash).ToArray(), CancellationToken.None);
//                var transactionReceipts = await receiptsTask;
//                if (receiptsTask.IsCanceled) return true;
//
//                if (receiptsTask.IsFaulted)
//                {
//                    if (receiptsTask.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
//                    {
//                        if (_logger.IsTrace) _logger.Error("Failed to retrieve receipts when synchronizing (Timeout)", receiptsTask.Exception);
//                    }
//                    else
//                    {
//                        if (_logger.IsError) _logger.Error("Failed to retrieve receipts when synchronizing", receiptsTask.Exception);
//                    }
//
//                    throw receiptsTask.Exception;
//                }
//
//                for (int blockIndex = 0; blockIndex < blocksWithTransactions.Length; blockIndex++)
//                {
//                    long gasUsedTotal = 0;
//                    for (int txIndex = 0; txIndex < blocksWithTransactions[blockIndex].Transactions.Length; txIndex++)
//                    {
//                        TransactionReceipt transactionReceipt = transactionReceipts[blockIndex][txIndex];
//                        if (transactionReceipt == null) throw new DataException($"Missing receipt for {blocksWithTransactions[blockIndex].Hash}->{txIndex}");
//
//                        transactionReceipt.Index = txIndex;
//                        transactionReceipt.BlockHash = blocksWithTransactions[blockIndex].Hash;
//                        transactionReceipt.BlockNumber = blocksWithTransactions[blockIndex].Number;
//                        transactionReceipt.TransactionHash = blocksWithTransactions[blockIndex].Transactions[txIndex].Hash;
//                        gasUsedTotal += transactionReceipt.GasUsed;
//                        transactionReceipt.GasUsedTotal = gasUsedTotal;
//                        transactionReceipt.Recipient = blocksWithTransactions[blockIndex].Transactions[txIndex].To;
//
//                        // only after execution
//                        // receipt.Sender = blocksWithTransactions[blockIndex].Transactions[txIndex].SenderAddress; 
//                        // receipt.Error = ...
//                        // receipt.ContractAddress = ...
//
//                        _receiptStorage.Add(transactionReceipt);
//                    }
//                }
//            }
//
//            return false;
//        }
//    }
//}