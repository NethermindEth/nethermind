//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Collections;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Timeouts = Nethermind.Synchronization.Timeouts;

namespace Nethermind.Merge.Plugin.Synchronization;

public class RefreshingPeerAllocationStrategy : IPeerAllocationStrategy
{
    private readonly IPeerAllocationStrategy _innerStrategy;
    private readonly IPivot _pivot;
    private readonly int _maxPeers;
    private readonly LruKeyCache<PeerInfo> _pivotPeerCache;
    private readonly UniqueConcurrentQueue<PeerInfo?> _uniqueConcurrentQueue;
    private readonly BlockingCollection<PeerInfo?> _peerRefreshQueue;
    private Task _refreshLoopTask = Task.CompletedTask;
    private readonly ILogger _logger;
    private INodeStatsManager? _stats;
    private Keccak? _lastPivotHash;

    public RefreshingPeerAllocationStrategy(
        IPeerAllocationStrategy innerStrategy,
        IPivot pivot, 
        int maxPeers,
        ILogManager logManager)
    {
        _uniqueConcurrentQueue = new();
        _peerRefreshQueue = new(_uniqueConcurrentQueue);
        
        _innerStrategy = innerStrategy;
        _pivot = pivot;
        _maxPeers = maxPeers;
        _pivotPeerCache = new LruKeyCache<PeerInfo>(
            maxPeers * 2,
            maxPeers,
            "pivotPeerCache");
        _logger = logManager.GetClassLogger();
        pivot.Changed += OnPivotChanged;
    }

    private void OnPivotChanged(object? sender, EventArgs e)
    {
        _pivotPeerCache.Clear();
    }

    public bool CanBeReplaced => true;

    public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
    {
        _stats = nodeStatsManager;
        
        if (_pivot.PivotHash is not null)
        {
            PeerInfo[] goodPeers = ArrayPool<PeerInfo>.Shared.Rent(_maxPeers);
            int goodPeersCount = 0;
            int peersToRefreshCount = 0;
            try
            {
                foreach (PeerInfo peer in peers)
                {
                    if (PeerHasPivot(peer))
                    {
                        goodPeers[goodPeersCount++] = peer;
                    }
                    else
                    {
                        if (!_uniqueConcurrentQueue.Contains(peer))
                        {
                            _peerRefreshQueue.TryAdd(peer);
                            peersToRefreshCount++;
                        }
                    }
                }

                if (peersToRefreshCount > 0)
                {
                    TryStart();
                }

                ArraySegment<PeerInfo> segment = new(goodPeers, 0, goodPeersCount);
                PeerInfo? allocatedPeer = _innerStrategy.Allocate(currentPeer, segment, nodeStatsManager, blockTree);
                if (_logger.IsTrace) _logger.Trace($"Managed to allocate peer {(allocatedPeer?.ToString() ?? "None")} from {goodPeersCount} candidates with same pivot");
                
                return allocatedPeer;
            }
            finally
            {
                ArrayPool<PeerInfo>.Shared.Return(goodPeers);
            }
        }
        else
        {
            return _innerStrategy.Allocate(currentPeer, peers, nodeStatsManager, blockTree);
        }
    }

    private void TryStart()
    {
        if (_refreshLoopTask.IsCompleted)
        {
            lock (_peerRefreshQueue)
            {
                if (_refreshLoopTask.IsCompleted)
                {
                    _refreshLoopTask = Task.Factory.StartNew(
                            RunRefreshPeerLoop,
                            CancellationToken.None,
                            TaskCreationOptions.LongRunning,
                            TaskScheduler.Default)
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                if (_logger.IsError) _logger.Error("Refreshing allocation loop encountered an exception.", t.Exception);
                            }
                        });
                }
            }
        }
    }
    
    private void RunRefreshPeerLoop()
    {
        using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromMinutes(5));
        foreach (PeerInfo? peer in _peerRefreshQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
        {
            if (peer is not null)
            {
                ISyncPeer syncPeer = peer.SyncPeer;
                
#pragma warning disable 4014
                ExecuteRefreshTask(peer).ContinueWith(t =>
#pragma warning restore 4014
                {
                    if (t.IsFaulted)
                    {
                        if (t.Exception != null && t.Exception.InnerExceptions.Any(x => x.InnerException is TimeoutException))
                        {
                            if (_logger.IsTrace) _logger.Trace($"Refreshing allocation info for {syncPeer} failed due to timeout: {t.Exception.Message}");
                        }
                        else if (_logger.IsDebug)
                        {
                            _logger.Debug($"Refreshing allocation info for {syncPeer} failed {t.Exception}");
                        }
                    }
                    else if (t.IsCanceled)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Refresh allocation info canceled: {syncPeer.Node:s}");
                    }

                    if (_logger.IsDebug) _logger.Debug($"Refreshed allocation info for {syncPeer}.");
                }, cancellationTokenSource.Token);
            }

            cancellationTokenSource.TryReset();
        }

        if (_logger.IsTrace) _logger.Trace("Exiting allocation peer refresh loop");
    }

    private async Task ExecuteRefreshTask(PeerInfo peer)
    {
        ISyncPeer syncPeer = peer.SyncPeer;
        if (_logger.IsTrace) _logger.Trace($"Requesting pivot block info from {syncPeer.Node:s}");

        CancellationTokenSource delaySource = new(Timeouts.RefreshPeer);
        Keccak? pivotPivotHash = _pivot.PivotHash;
        if (pivotPivotHash is not null)
        {
            Task<BlockHeader?> getHeadHeaderTask = syncPeer.GetBlockHeader(pivotPivotHash, delaySource.Token);
            await getHeadHeaderTask.ContinueWith(
                t =>
                {
                    try
                    {
                        if (t.IsCanceled)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Refresh allocation timed out for node: {syncPeer.Node:c}");
                            _stats?.ReportSyncEvent(syncPeer.Node, syncPeer.IsInitialized ? NodeStatsEventType.SyncFailed : NodeStatsEventType.SyncInitFailed);
                            syncPeer.Disconnect(DisconnectReason.DisconnectRequested, "refresh peer info fault - timeout");
                        }
                        else if (t.IsFaulted)
                        {
                            if (_logger.IsTrace) _logger.Trace($"Refresh allocation failed for node: {syncPeer.Node:c}{Environment.NewLine}{t.Exception}");
                            _stats?.ReportSyncEvent(syncPeer.Node, syncPeer.IsInitialized ? NodeStatsEventType.SyncFailed : NodeStatsEventType.SyncInitFailed);
                            syncPeer.Disconnect(DisconnectReason.DisconnectRequested, "refresh peer info fault - faulted");
                        }
                        else
                        {
                            delaySource.Cancel();
                            BlockHeader? header = getHeadHeaderTask.Result;
                            if (header == null)
                            {
                                if (_logger.IsTrace) _logger.Trace($"Refresh allocation failed for node: {syncPeer.Node:c}{Environment.NewLine}{t.Exception}");
                                _stats?.ReportSyncEvent(syncPeer.Node, syncPeer.IsInitialized ? NodeStatsEventType.SyncFailed : NodeStatsEventType.SyncInitFailed);
                                syncPeer.Disconnect(DisconnectReason.DisconnectRequested, "refresh peer info fault - null response");
                                return;
                            }
                            else if (!HeaderValidator.ValidateHash(header))
                            {
                                _stats?.ReportSyncEvent(syncPeer.Node, syncPeer.IsInitialized ? NodeStatsEventType.SyncFailed : NodeStatsEventType.SyncInitFailed);
                                syncPeer.Disconnect(DisconnectReason.DisconnectRequested, "refresh peer info fault - invalid header");
                                return;
                            }

                            if (_logger.IsTrace) _logger.Trace($"Received pivot block info from {syncPeer.Node:c} with pivot block {header.ToString(BlockHeader.Format.Short)}, total difficulty {(header.TotalDifficulty.ToString() ?? "None")}");
                            if (!syncPeer.IsInitialized) _stats?.ReportSyncEvent(syncPeer.Node, NodeStatsEventType.SyncInitCompleted);
                            if (_logger.IsTrace) _logger.Trace($"REFRESH Updating header of {syncPeer} from {syncPeer.HeadNumber} to {header.Number}");

                            if (header.Hash == pivotPivotHash)
                            {
                                _pivotPeerCache.Set(peer);
                            }
                            else
                            {
                                if (_logger.IsDebug) _logger.Debug($"Refresh allocation failed for node: {syncPeer.Node:c}, pivot block hash mismatch");
                                _stats?.ReportSyncEvent(syncPeer.Node, syncPeer.IsInitialized ? NodeStatsEventType.SyncFailed : NodeStatsEventType.SyncInitFailed);
                                syncPeer.Disconnect(DisconnectReason.DisconnectRequested, "refresh peer info fault - different pivot block hash");
                            }
                        }
                    }
                    finally
                    {
                        delaySource.Dispose();
                    }
                }, delaySource.Token);
        }
    }    

    private bool PeerHasPivot(PeerInfo peer)
    {
        long pivotNumber = _pivot.PivotNumber;
        UInt256? pivotTotalDifficulty = _pivot.PivotTotalDifficulty;
        Keccak? pivotHash = _pivot.PivotHash;
        if (_logger.IsDebug)
        {
            if (pivotHash != _lastPivotHash)
            {
                _logger.Debug($"Pivot changed to {pivotHash} from {_lastPivotHash}");
            }

            _lastPivotHash = pivotHash;
        }
        bool peerIsPastPivotNumber = peer.HeadNumber >= pivotNumber;
        bool peerIsPastPivotTotalDifficulty = pivotTotalDifficulty is null || peer.TotalDifficulty >= pivotTotalDifficulty;
        if (peerIsPastPivotNumber && peerIsPastPivotTotalDifficulty)
        {
            return peer.HeadNumber == pivotNumber 
                ? peer.HeadHash == pivotHash 
                : _pivotPeerCache.Get(peer);
        }

        return false;
    }
}
