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

namespace Nethermind.Blockchain.Synchronization
{
    public class FullArchiveSynchronizer : IFullArchiveSynchronizer
    {
        private int _sinceLastTimeout;
        private UInt256 _lastSyncNumber = UInt256.Zero;

        private readonly ILogger _logger;
        private readonly IBlockValidator _blockValidator;
        private readonly ISealValidator _sealValidator;
        private readonly IEthSyncPeerPool _peerPool;

        private readonly ITransactionValidator _transactionValidator;
        private readonly Blockchain.ISyncConfig _syncConfig;
        private readonly IBlockTree _blockTree;

        private int _currentBatchSize = 256;

        public const int MinBatchSize = 8;

        public const int MaxBatchSize = 512;

        public const int MaxReorganizationLength = 2 * MaxBatchSize;

        private void IncreaseBatchSize() => _currentBatchSize = Math.Min(MaxBatchSize, _currentBatchSize * 2);

        private void DecreaseBatchSize() => _currentBatchSize = Math.Max(MinBatchSize, _currentBatchSize / 2);

        public FullArchiveSynchronizer(
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            ISealValidator sealValidator,
            ITransactionValidator transactionValidator,
            ILogManager logManager,
            Blockchain.ISyncConfig syncConfig,
            IEthSyncPeerPool peerPool)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _peerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));

            _transactionValidator = transactionValidator ?? throw new ArgumentNullException(nameof(transactionValidator));

            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
        }
        
        private CancellationTokenSource _syncLoopCancelTokenSource = new CancellationTokenSource();
        
        private Task _syncLoopTask;

        private bool _isInitialized;
        
        public async Task StopAsync()
        {
            _isInitialized = false;
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
            _isInitialized = true;

            _syncLoopTask = Task.Factory.StartNew(
                RunSyncLoop,
                _syncLoopCancelTokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ContinueWith(t =>
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
                    var initPeerCount = _peerPool.AllPeers.Count(p => p.IsInitialized);

                    if (DateTime.UtcNow - _lastFullInfo > TimeSpan.FromSeconds(120) && _logger.IsDebug)
                    {
                        if (_logger.IsDebug) _logger.Debug("Sync peers:");
                        foreach (PeerInfo peerInfo in _peerPool.AllPeers)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{peerInfo}");
                        }

                        _lastFullInfo = DateTime.UtcNow;
                    }
                    else if (initPeerCount != _lastSyncPeersCount)
                    {
                        _lastSyncPeersCount = initPeerCount;
                        if (_logger.IsInfo) _logger.Info($"Sync peers {initPeerCount}({_peerPool.PeerCount})/{_syncConfig.SyncPeersMaxCount} {(_allocation.Current != null ? $"(sync in progress with {_allocation.Current})" : string.Empty)}");
                    }
                    else if (initPeerCount == 0)
                    {
                        if (_logger.IsInfo) _logger.Info($"Sync peers 0, searching for peers to sync with...");
                    }
                    
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
        public void RequestSynchronization()
        {
            _syncRequested.Set();
        }

        private readonly ManualResetEventSlim _syncRequested = new ManualResetEventSlim(false);

        private SyncPeerAllocation _allocation;
        
        private async Task RunSyncLoop()
        {
            _allocation = _peerPool.BorrowPeer(_blockTree.BestSuggested?.TotalDifficulty ?? 0);
            _allocation.Replaced += AllocationOnReplaced;
            _allocation.Cancelled += AllocationOnCancelled;
            
            while (true)
            {
                if (_logger.IsTrace) _logger.Trace("Sync loop - next iteration.");
                _syncRequested.Wait(_syncLoopCancelTokenSource.Token);
                _syncRequested.Reset();
                
                /* If block tree is processing blocks from DB then we are not going to start the sync process.
                 * In the future it may make sense to run sync anyway and let DB loader know that there are more blocks waiting.
                 * */

                if (!_blockTree.CanAcceptNewBlocks) continue;

                if (_allocation.Current == null) continue;

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
                                $"best peer block #: {(_peerPool.AllPeers.Any() ? _peerPool.AllPeers.Max(x => x.HeadNumber) : 0)}");
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

                            _peerPool.RemovePeer(peerInfo.SyncPeer);
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
                                _peerPool.RemovePeer(peerInfo.SyncPeer);
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
            }
        }

        private void AllocationOnCancelled(object sender, AllocationChangeEventArgs e)
        {
            if (_logger.IsDebug) _logger.Debug($"Cancelling {e.Previous} sync.");
            _peerSyncCancellationTokenSource?.Cancel();
        }

        private void  AllocationOnReplaced(object sender, AllocationChangeEventArgs e)
        {
            if (_logger.IsWarn) _logger.Warn($"Replacing {e.Previous} sync with {e.Current}.");
            _requestedSyncCancelDueToBetterPeer = true;
            _peerSyncCancellationTokenSource?.Cancel();
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
                        if (_logger.IsInfo) _logger.Info($"Received no blocks from {_allocation.Current} in response to {blocksToRequest} blocks requested. Cancelling.");
                        throw new EthSynchronizationException("Peer sent an empty block list");
                    }

                    if (_logger.IsInfo) _logger.Info($"Received no blocks from {_allocation.Current} in response to {blocksToRequest} blocks requested. Decreasing batch size from {_currentBatchSize}.");
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