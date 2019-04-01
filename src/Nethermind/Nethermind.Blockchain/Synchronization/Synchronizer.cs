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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Logging;
using ILogger = Nethermind.Core.Logging.ILogger;

namespace Nethermind.Blockchain.Synchronization
{
    public class Synchronizer : ISynchronizer
    {
        private readonly IFullArchiveSynchronizer _fullArchiveSynchronizer;
        private readonly IFastSynchronizer _fastSynchronizer;
        private readonly ILogger _logger;
        public SynchronizationMode CurrentSynchronizationMode { get; set; }

        public Synchronizer(IFullArchiveSynchronizer fullArchiveSynchronizer, IFastSynchronizer fastSynchronizer, ILogManager logManager)
        {
            _fullArchiveSynchronizer = fullArchiveSynchronizer ?? throw new ArgumentNullException(nameof(fullArchiveSynchronizer));
            _fastSynchronizer = fastSynchronizer ?? throw new ArgumentNullException(nameof(fastSynchronizer));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private readonly ManualResetEventSlim _syncRequested = new ManualResetEventSlim(false);
        
        private CancellationTokenSource _syncLoopCancelTokenSource = new CancellationTokenSource();
        private CancellationTokenSource _peerSyncCancellationTokenSource;
        
        private Task _syncLoopTask;
        
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
        
        private async Task RunSyncLoop()
        {
            while (true)
            {
                if (_logger.IsTrace) _logger.Trace("Sync loop - next iteration.");
                _syncRequested.Wait(_syncLoopCancelTokenSource.Token);
                _syncRequested.Reset();
                /* If block tree is processing blocks from DB then we are not going to start the sync process.
                 * In the future it may make sense to run sync anyway and let DB loader know that there are more blocks waiting.
                 * */

                if (!_blockTree.CanAcceptNewBlocks) continue;

                if (!HasAnyPeersToSyncWith()) continue;

                while (true)
                {
                    if (_syncLoopCancelTokenSource.IsCancellationRequested)
                    {
                        if (_logger.IsTrace) _logger.Trace("Sync loop cancellation requested - leaving.");
                        break;
                    }

                    var peerInfo = _currentSyncingPeerInfo = SelectBestPeerForSync();
                    if (peerInfo == null)
                    {
                        if (_logger.IsDebug)
                            _logger.Debug(
                                "No more peers with better block available, finishing sync process, " +
                                $"best known block #: {_blockTree.BestKnownNumber}, " +
                                $"best peer block #: {(_peers.Values.Any() ? _peers.Values.Max(x => x.HeadNumber) : 0)}");
                        break;
                    }

                    SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, SyncStatus.Started)
                    {
                        NodeBestBlockNumber = peerInfo.HeadNumber,
                        OurBestBlockNumber = _blockTree.BestKnownNumber
                    });

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

                            _selector.RemovePeer(peerInfo.SyncPeer);
                            if (_logger.IsTrace) _logger.Trace($"Sync with {peerInfo} failed. Removed node from sync peers.");
                            SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, SyncStatus.Failed)
                            {
                                NodeBestBlockNumber = peerInfo.HeadNumber,
                                OurBestBlockNumber = _blockTree.BestKnownNumber
                            });
                        }
                        else if (t.IsCanceled || _peerSyncCancellationTokenSource.IsCancellationRequested)
                        {
                            if (_requestedSyncCancelDueToBetterPeer)
                            {
                                if (_logger.IsDebug) _logger.Debug($"Cancelled sync with {_currentSyncingPeerInfo} due to connection with better peer.");
                                _requestedSyncCancelDueToBetterPeer = false;
                            }
                            else
                            {
                                RemovePeer(peerInfo.SyncPeer);
                                if (_logger.IsTrace) _logger.Trace($"Sync with {peerInfo} canceled. Removed node from sync peers.");
                                SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, SyncStatus.Cancelled)
                                {
                                    NodeBestBlockNumber = peerInfo.HeadNumber,
                                    OurBestBlockNumber = _blockTree.BestKnownNumber
                                });
                            }
                        }
                        else if (t.IsCompleted)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Sync process finished with {peerInfo}. Best known block is ({_blockTree.BestKnownNumber})");
                            SyncEvent?.Invoke(this, new SyncEventArgs(peerInfo.SyncPeer, SyncStatus.Completed)
                            {
                                NodeBestBlockNumber = peerInfo.HeadNumber,
                                OurBestBlockNumber = _blockTree.BestKnownNumber
                            });
                        }

                        if (_logger.IsDebug)
                            _logger.Debug(
                                $"Finished peer sync process [{(t.IsFaulted ? "FAULTED" : t.IsCanceled ? "CANCELED" : t.IsCompleted ? "COMPLETED" : "OTHER")}] with {peerInfo}], " +
                                $"best known block #: {_blockTree.BestKnownNumber} ({_blockTree.BestKnownNumber}), " +
                                $"best peer block #: {peerInfo.HeadNumber} ({peerInfo.HeadNumber})");

                        var source = _peerSyncCancellationTokenSource;
                        _peerSyncCancellationTokenSource = null;
                        source?.Dispose();
                    }, _syncLoopCancelTokenSource.Token);
                }
            }
        }

        public void Start()
        {
            _isInitialized = true;
            _blockTree.NewHeadBlock += OnNewHeadBlock;

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
        
        private void StartSyncTimer()
        {
            if (_logger.IsDebug) _logger.Debug("Starting sync timer");
            _syncTimer = new System.Timers.Timer(_syncConfig.SyncTimerInterval);
            _syncTimer.Elapsed += (s, e) =>
            {
                try
                {
                    _syncTimer.Enabled = false;
                    var initPeerCount = _peers.Count(p => p.Value.IsInitialized);

                    if (DateTime.Now - _lastFullInfo > TimeSpan.FromSeconds(120) && _logger.IsDebug)
                    {
                        if (_logger.IsDebug) _logger.Debug("Sync peers list:");
                        foreach ((PublicKey nodeId, PeerInfo peerInfo) in _peers)
                        {
                            if (_logger.IsDebug) _logger.Debug($"{peerInfo}");
                        }

                        _lastFullInfo = DateTime.Now;
                    }
                    else if (initPeerCount != _lastSyncPeersCount)
                    {
                        _lastSyncPeersCount = initPeerCount;
                        if (_logger.IsInfo) _logger.Info($"Sync peers {initPeerCount}({_peers.Count})/{_syncConfig.SyncPeersMaxCount} {(_currentSyncingPeerInfo != null ? $"(sync in progress with {_currentSyncingPeerInfo})" : string.Empty)}");
                    }
                    else if (initPeerCount == 0)
                    {
                        if (_logger.IsInfo) _logger.Info($"Sync peers 0, searching for peers to sync with...");
                    }

                    CheckIfSyncingWithFastestPeer();
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

        public void RequestSynchronization()
        {
            throw new System.NotImplementedException();
        }
    }
}