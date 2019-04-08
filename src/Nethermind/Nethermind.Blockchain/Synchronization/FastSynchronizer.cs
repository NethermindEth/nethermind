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
using System.Timers;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Mining;

namespace Nethermind.Blockchain.Synchronization
{
    /// <summary>
    /// Responsible for fast sync processing.
    /// Fast sync starts with headers download followed by node data (state DB + code DB) downloads and then
    /// switches to full sync mode.
    /// </summary>
    public class FastSynchronizer : ISynchronizer, INodeDataRequestExecutor, IDisposable
    {
        private const int MinBatchSize = 8;

        /* set to 512 because other clients do not respond to larger batch sizes */
        private const int MaxBatchSize = 512;

        private const int MaxReorganizationLength = 2 * MaxBatchSize;

        /* headers batch can start at max as headers are predictable in size, unlike blocks */
        private int _currentBatchSize = MaxBatchSize;

        private TimeSpan _fullPeerListInterval = TimeSpan.FromSeconds(120);
        private DateTime _timeOfTheLastFullPeerListLogEntry = DateTime.UtcNow;
        private long _lastSyncNumber;
        private int _lastSyncPeersCount;
        private int _sinceLastTimeout;

        private readonly ILogger _logger;
        private readonly ISyncConfig _syncConfig;
        private readonly IHeaderValidator _headerValidator;
        private readonly ISealValidator _sealValidator;
        private readonly IEthSyncPeerPool _syncPeerPool;
        private readonly ISynchronizer _fullSynchronizer;
        private readonly INodeDataDownloader _nodeDataDownloader;
        private readonly IBlockTree _blockTree;

        private System.Timers.Timer _syncTimer;
        private SyncPeerAllocation _allocation;
        private Task _syncLoopTask;
        private CancellationTokenSource _syncLoopCancellation = new CancellationTokenSource();
        private CancellationTokenSource _peerSyncCancellation;
        private bool _requestedSyncCancelDueToBetterPeer;
        private readonly ManualResetEventSlim _syncRequested = new ManualResetEventSlim(false);
        private SynchronizationMode _mode = SynchronizationMode.Blocks;

        /* sync events are used mainly for managing sync peers reputation */
        public event EventHandler<SyncEventArgs> SyncEvent;

        public FastSynchronizer(IBlockTree blockTree,
            IHeaderValidator headerValidator,
            ISealValidator sealValidator,
            IEthSyncPeerPool peerPool,
            ISyncConfig syncConfig,
            INodeDataDownloader nodeDataDownloader,
            ISynchronizer fullSynchronizer,
            ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _syncConfig = syncConfig ?? throw new ArgumentNullException(nameof(syncConfig));
            _fullSynchronizer = fullSynchronizer ?? throw new ArgumentNullException(nameof(fullSynchronizer));
            _nodeDataDownloader = nodeDataDownloader ?? throw new ArgumentNullException(nameof(nodeDataDownloader));
            _syncPeerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));

            _nodeDataDownloader.SetExecutor(this);
            _fullSynchronizer.SyncEvent += (s, e) => SyncEvent?.Invoke(this, e);
        }

        public bool IsInitialSyncFinished { get; private set; }

        /// <summary>
        /// Can be increased when the sync has not timed a few times in a row.
        /// </summary>
        private void IncreaseBatchSize()
        {
            if (_logger.IsDebug) _logger.Debug($"Changing header request batch size to {_currentBatchSize}");
            _currentBatchSize = Math.Min(MaxBatchSize, _currentBatchSize * 2);
        }

        /// <summary>
        /// Decreases header request batch size after timeout in case the sync peer is not able to deliver
        /// more headers at once.
        /// </summary>
        private void DecreaseBatchSize()
        {
            if (_logger.IsDebug) _logger.Debug($"Changing header request batch size to {_currentBatchSize}");
            _currentBatchSize = Math.Max(MinBatchSize, _currentBatchSize / 2);
        }

        public async Task StopAsync()
        {
            await _fullSynchronizer.StopAsync();
            await StopFastSync();
        }

        /// <summary>
        /// This is invoked when the node data download phase finalizes and we can transition to full sync.
        /// </summary>
        private async Task StopFastSync()
        {
            StopSyncTimer();

            _peerSyncCancellation?.Cancel();
            _syncLoopCancellation?.Cancel();

            await (_syncLoopTask ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Fast sync stopped...");
        }

        private void StopSyncTimer()
        {
            try
            {
                if (_logger.IsDebug) _logger.Debug("Stopping fast sync timer");
                _syncTimer?.Stop();
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Error during the fast sync timer stop", e);
            }
        }

        public void Start()
        {
            AllocateSyncPeerPool();
            
            // Task.Run may cause trouble - make sure to test it well if planning to uncomment 
            // _syncLoopTask = Task.Run(RunSyncLoop, _syncLoopCancelTokenSource.Token) 
            _syncLoopTask = Task.Factory.StartNew(
                    RunFastSyncLoop,
                    _syncLoopCancellation.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap()
                .ContinueWith(task =>
                {
                    switch (task)
                    {
                        case Task t when t.IsFaulted:
                            if (_logger.IsError) _logger.Error("Sync loop encountered an exception.", t.Exception);
                            break;
                        case Task t when t.IsCanceled:
                            if (_logger.IsInfo) _logger.Info("Sync loop canceled.");
                            break;
                        case Task t when t.IsCompletedSuccessfully:
                            if (_logger.IsInfo) _logger.Info("Sync loop completed successfully.");
                            break;
                        default:
                            if (_logger.IsInfo) _logger.Info("Sync loop completed.");
                            break;
                    }
                });

            StartSyncTimer();
        }

        private void StartSyncTimer()
        {
            if (_logger.IsDebug) _logger.Debug($"Starting fast sync timer with interval of {_syncConfig.SyncTimerInterval}ms");
            _syncTimer = new System.Timers.Timer(_syncConfig.SyncTimerInterval);
            _syncTimer.Elapsed += SyncTimerOnElapsed;
            _syncTimer.Start();
        }

        private void SyncTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            _syncTimer.Enabled = false;
            
            try
            {
                LogSyncPeers();
                RequestSynchronization("[SYNC TIMER]");
            }
            catch (Exception ex)
            {
                if (_logger.IsDebug) _logger.Error("Sync timer failed", ex);
            }
            finally
            {
                _syncTimer.Enabled = true;
            }
        }

        private void LogSyncPeers()
        {
            var initPeerCount = _syncPeerPool.AllPeers.Count(p => p.IsInitialized);
            if (DateTime.UtcNow - _timeOfTheLastFullPeerListLogEntry > _fullPeerListInterval && _logger.IsDebug)
            {
                if (_logger.IsDebug) _logger.Debug("Sync peers:");
                foreach (PeerInfo peerInfo in _syncPeerPool.AllPeers)
                {
                    string prefix = peerInfo == _allocation.Current
                        ? "[SYNCING] "
                        : string.Empty;

                    if (_logger.IsDebug) _logger.Debug($"{prefix}{peerInfo}");
                }

                _timeOfTheLastFullPeerListLogEntry = DateTime.UtcNow;
            }
            else if (initPeerCount != _lastSyncPeersCount)
            {
                _lastSyncPeersCount = initPeerCount;
                if (_logger.IsInfo) _logger.Info($"Sync peers {initPeerCount}({_syncPeerPool.PeerCount})/{_syncConfig.SyncPeersMaxCount} {(_allocation.Current != null ? $"(sync in progress with {_allocation.Current})" : string.Empty)}");
            }
            else if (initPeerCount == 0)
            {
                if (_logger.IsInfo) _logger.Info($"Sync peers 0({_syncPeerPool.PeerCount})/{_syncConfig.SyncPeersMaxCount}, searching for peers to sync with...");
            }
        }

        /// <summary>
        /// Notifies synchronizer that an event occured that should trigger synchronization
        /// at the nearest convenient time.
        /// </summary>
        /// <param name="reason">Reason for the synchronization request for logging</param>
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

        private async Task RunFastSyncLoop()
        {
            while (true)
            {
                if (_logger.IsTrace) _logger.Trace("Sync loop - next iteration WAIT.");
                _syncRequested.Wait(_syncLoopCancellation.Token);
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
                    if (_syncLoopCancellation.IsCancellationRequested)
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

                    _peerSyncCancellation = new CancellationTokenSource();
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
                        else if (t.IsCanceled || _peerSyncCancellation.IsCancellationRequested)
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

                        var source = _peerSyncCancellation;
                        _peerSyncCancellation = null;
                        source?.Dispose();
                    }, _syncLoopCancellation.Token);
                }

                _allocation.FinishSync();
                if (_logger.IsInfo) _logger.Info($"[FAST SYNC] Current sync at {_blockTree.BestSuggested?.Number}!");
                if ((_blockTree.BestSuggested?.Number ?? 0) > 0) // make it 1024 * 128 or configurable for tests
                {
                    _syncPeerPool.EnsureBest(_allocation, (_blockTree.BestSuggested?.TotalDifficulty - 1) ?? 0);
                    if ((_allocation.Current?.HeadNumber ?? 0) <= (_blockTree.BestSuggested?.Number ?? 0) + 1024)
                    {
                        BlockHeader bestSuggested = _blockTree.BestSuggested;
                        if (bestSuggested == null)
                        {
                            if (_logger.IsError) _logger.Error("Best suggested block is null when starting fast sync!");
                            throw new EthSynchronizationException("Best suggested block is null when starting fast sync!");
                        }

                        if (_logger.IsInfo) _logger.Info($"[FAST SYNC] Switching to node data download at block {bestSuggested.Number}!");
                        foreach (PeerInfo peerInfo in _syncPeerPool.AllPeers)
                        {
                            if (_logger.IsInfo) _logger.Info($"[FAST SYNC] Peers:");
                            if (_logger.IsInfo) _logger.Info($"[FAST SYNC] {peerInfo}!");
                        }

                        List<Keccak> stateRoots = new List<Keccak>();

                        stateRoots.Add(bestSuggested.StateRoot);
//                        for (int i = 0; i < 64; i++)
//                        {
//                            stateRoots.Add(_blockTree.FindHeader(bestSuggested.ParentHash).StateRoot);
//                        }

                        _mode = SynchronizationMode.NodeData;
                        await _nodeDataDownloader.SyncNodeData(stateRoots.Select<Keccak, (Keccak, NodeDataType)>(sr => (sr, NodeDataType.State)).ToArray()).ContinueWith(
                            t =>
                            {
                                PeerInfo current = _allocation.Current;
                                if (t.IsFaulted && _allocation.Current != null)
                                {
                                    _syncPeerPool.RemovePeer(current.SyncPeer);
                                    if (_logger.IsTrace) _logger.Trace($"Sync with {current} failed. Removed node from sync peers.");
                                    SyncEvent?.Invoke(this, new SyncEventArgs(current.SyncPeer, SyncStatus.Failed));
                                }
                                else
                                {
                                    _mode = SynchronizationMode.Full;

                                    _allocation.Replaced -= AllocationOnReplaced;
                                    _allocation.Cancelled -= AllocationOnCancelled;
                                    _syncPeerPool.ReturnPeer(_allocation);

                                    if (_logger.IsInfo) _logger.Info($"[FAST SYNC] complete");

                                    // avoid deadlocking here
#pragma warning disable 4014
                                    StopFastSync().ContinueWith(stopTask =>
#pragma warning restore 4014
                                    {
                                        _fullSynchronizer.Start();
                                        _fullSynchronizer.RequestSynchronization("fast sync complete");
                                        IsInitialSyncFinished = true;
                                    });
                                }
                            });
                    }
                }
            }
        }
        
        private void AllocateSyncPeerPool()
        {
            if (_logger.IsDebug) _logger.Debug("Initializing fast sync loop.");
            _allocation = _syncPeerPool.BorrowPeer("fast sync");
            if (_logger.IsDebug) _logger.Debug("Fast sync loop allocated.");
            _allocation.Replaced += AllocationOnReplaced;
            _allocation.Cancelled += AllocationOnCancelled;
        }

        private void AllocationOnCancelled(object sender, AllocationChangeEventArgs e)
        {
            if (_logger.IsDebug) _logger.Debug($"Cancelling {e.Previous} on {_allocation}.");
            _peerSyncCancellation?.Cancel();
        }

        private void AllocationOnReplaced(object sender, AllocationChangeEventArgs e)
        {
            if (e.Previous == null)
            {
                if (_logger.IsDebug) _logger.Debug($"Allocating {e.Current} on {_allocation}.");
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug($"Replacing {e.Previous} with {e.Current} on {_allocation}.");
            }

            if (e.Previous != null)
            {
                _requestedSyncCancelDueToBetterPeer = true;
                _peerSyncCancellation?.Cancel();
            }

            PeerInfo newPeer = e.Current;
            if (newPeer.TotalDifficulty > _blockTree.BestSuggested.TotalDifficulty)
            {
                RequestSynchronization("[REPLACE]");
            }
        }

        [Todo(Improve.Readability, "Review cancellation")]
        private async Task SynchronizeWithPeerAsync(PeerInfo peerInfo)
        {
            if (_logger.IsDebug) _logger.Debug($"Starting sync process with {peerInfo} - theirs {peerInfo.HeadNumber} {peerInfo.TotalDifficulty} | ours {_blockTree.BestSuggested.Number} {_blockTree.BestSuggested.TotalDifficulty}");
            bool wasCanceled = false;

            ISyncPeer peer = peerInfo.SyncPeer;

            int ancestorLookupLevel = 0;
            int emptyBlockListCounter = 0;

            // fast sync 16 (BetsKnown + 16 below) here - review where it should be added
            long currentNumber = Math.Min(_blockTree.BestKnownNumber + 16, peerInfo.HeadNumber - 1);
            while (peerInfo.TotalDifficulty > (_blockTree.BestSuggested?.TotalDifficulty ?? 0) && currentNumber <= peerInfo.HeadNumber)
            {
                if (_logger.IsTrace) _logger.Trace($"Continue syncing with {peerInfo} (our best {_blockTree.BestKnownNumber})");

                if (ancestorLookupLevel > MaxReorganizationLength)
                {
                    if (_logger.IsWarn) _logger.Warn($"Could not find common ancestor with {peerInfo}");
                    throw new EthSynchronizationException("Peer with inconsistent chain in sync");
                }

                if (_peerSyncCancellation.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"Sync with {peerInfo} cancelled");
                    return;
                }

                long blocksLeft = peerInfo.HeadNumber - currentNumber;
                int blocksToRequest = (int) BigInteger.Min(blocksLeft + 1, _currentBatchSize);
                if (_logger.IsTrace) _logger.Trace($"Sync request {currentNumber}+{blocksToRequest} to peer {peerInfo.SyncPeer.Node.Id} with {peerInfo.HeadNumber} blocks. Got {currentNumber} and asking for {blocksToRequest} more.");

                Task<BlockHeader[]> headersTask = peer.GetBlockHeaders(currentNumber, blocksToRequest, 0, _peerSyncCancellation.Token);
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

                if (_peerSyncCancellation.IsCancellationRequested)
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
                    if (_peerSyncCancellation.IsCancellationRequested)
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
                    if (_peerSyncCancellation.IsCancellationRequested)
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
                    if (_peerSyncCancellation.IsCancellationRequested)
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
                await peer.GetNodeData(hashes, _syncLoopCancellation.Token);
            return request;
        }

        public void Dispose()
        {
            _syncTimer?.Dispose();
            _syncLoopTask?.Dispose();
            _syncLoopCancellation?.Dispose();
            _peerSyncCancellation?.Dispose();
            _syncRequested?.Dispose();
        }
    }
}

//  private async Task<bool> DownloadReceipts(Block[] blocks, ISyncPeer peer)
//  {
//      var blocksWithTransactions = blocks.Where(b => b.Transactions.Length != 0).ToArray();
//      if (blocksWithTransactions.Length != 0)
//      {
//          var receiptsTask = peer.GetReceipts(blocksWithTransactions.Select(b => b.Hash).ToArray(), CancellationToken.None);
//          var transactionReceipts = await receiptsTask;
//          if (receiptsTask.IsCanceled) return true;
//
//          if (receiptsTask.IsFaulted)
//          {
//              if (receiptsTask.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
//              {
//                  if (_logger.IsTrace) _logger.Error("Failed to retrieve receipts when synchronizing (Timeout)", receiptsTask.Exception);
//              }
//              else
//              {
//                  if (_logger.IsError) _logger.Error("Failed to retrieve receipts when synchronizing", receiptsTask.Exception);
//              }
//
//              throw receiptsTask.Exception;
//          }
//
//          for (int blockIndex = 0; blockIndex < blocksWithTransactions.Length; blockIndex++)
//          {
//              long gasUsedTotal = 0;
//              for (int txIndex = 0; txIndex < blocksWithTransactions[blockIndex].Transactions.Length; txIndex++)
//              {
//                  TransactionReceipt transactionReceipt = transactionReceipts[blockIndex][txIndex];
//                  if (transactionReceipt == null) throw new DataException($"Missing receipt for {blocksWithTransactions[blockIndex].Hash}->{txIndex}");
//
//                  transactionReceipt.Index = txIndex;
//                  transactionReceipt.BlockHash = blocksWithTransactions[blockIndex].Hash;
//                  transactionReceipt.BlockNumber = blocksWithTransactions[blockIndex].Number;
//                  transactionReceipt.TransactionHash = blocksWithTransactions[blockIndex].Transactions[txIndex].Hash;
//                  gasUsedTotal += transactionReceipt.GasUsed;
//                  transactionReceipt.GasUsedTotal = gasUsedTotal;
//                  transactionReceipt.Recipient = blocksWithTransactions[blockIndex].Transactions[txIndex].To;
//
//                  // only after execution
//                  // receipt.Sender = blocksWithTransactions[blockIndex].Transactions[txIndex].SenderAddress; 
//                  // receipt.Error = ...
//                  // receipt.ContractAddress = ...
//
//                  _receiptStorage.Add(transactionReceipt);
//              }
//          }
//      }
//
//      return false;
//  }