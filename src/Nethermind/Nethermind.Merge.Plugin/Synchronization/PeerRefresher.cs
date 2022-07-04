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
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;

[assembly: InternalsVisibleTo("Nethermind.Merge.Plugin.Test")]
namespace Nethermind.Merge.Plugin.Synchronization;

public class PeerRefresher : IPeerRefresher, IAsyncDisposable
{
    private const int RefreshTimeout = 3000; // the Eth.Timeout hits us at 5000 (or whatever it is configured to)
    private readonly ISyncPeerPool _syncPeerPool;
    private static readonly TimeSpan _minRefreshDelay = TimeSpan.FromSeconds(10);
    private DateTime _lastRefresh = DateTime.MinValue;
    private (Keccak, Keccak, Keccak) _lastBlockhashes = (Keccak.Zero, Keccak.Zero, Keccak.Zero);
    private readonly ITimer _refreshTimer;
    private ILogger _logger;

    public PeerRefresher(ISyncPeerPool syncPeerPool, ITimerFactory timerFactory, ILogManager logManager)
    {
        _refreshTimer = timerFactory.CreateTimer(_minRefreshDelay);
        _refreshTimer.Elapsed += TimerOnElapsed;
        _refreshTimer.AutoReset = false;
        _syncPeerPool = syncPeerPool;
        _logger = logManager.GetClassLogger(GetType());
    }

    public void RefreshPeers(Keccak headBlockhash, Keccak headParentBlockhash, Keccak finalizedBlockhash)
    {
        _lastBlockhashes = (headBlockhash, headParentBlockhash, finalizedBlockhash);
        TimeSpan timePassed = DateTime.Now - _lastRefresh;
        if (timePassed > _minRefreshDelay)
        {
            Refresh(headBlockhash, headParentBlockhash, finalizedBlockhash);
        }
        else if (!_refreshTimer.Enabled)
        {
            _refreshTimer.Interval = _minRefreshDelay - timePassed;
            _refreshTimer.Start();
        }
    }

    private void TimerOnElapsed(object? sender, EventArgs e)
    {
        Refresh(_lastBlockhashes.Item1, _lastBlockhashes.Item2, _lastBlockhashes.Item3);
    }
    
    private void Refresh(Keccak headBlockhash, Keccak headParentBlockhash, Keccak finalizedBlockhash)
    {
        _lastRefresh = DateTime.Now;
        foreach (PeerInfo peer in _syncPeerPool.AllPeers)
        {
            StartPeerRefreshTask(peer.SyncPeer, headBlockhash, headParentBlockhash, finalizedBlockhash);
        }
    }

    private async Task StartPeerRefreshTask(
        ISyncPeer syncPeer,
        Keccak headBlockhash,
        Keccak headParentBlockhash,
        Keccak finalizedBlockhash
    )
    {
        Task.Run(async () =>
        {
            CancellationTokenSource delaySource = new();
            Task delayTask = Task.Delay(RefreshTimeout, delaySource.Token);
            try
            {
                await RefreshPeerForFcu(syncPeer, headBlockhash, headParentBlockhash, finalizedBlockhash, delayTask, delaySource.Token);
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error($"Exception in peer refresh. This is unexpected. {syncPeer}", exception);
            }
            finally
            {
                delaySource.Cancel();
            }
        });
    }

    internal async Task RefreshPeerForFcu(
        ISyncPeer syncPeer,
        Keccak headBlockhash,
        Keccak headParentBlockhash,
        Keccak finalizedBlockhash,
        Task? delayTask,
        CancellationToken token
    ) {
        
        // headBlockhash is obtained together with headParentBlockhash
        Task<BlockHeader[]> getHeadParentHeaderTask = syncPeer.GetBlockHeaders(headParentBlockhash, 2, 0, token);
        Task<BlockHeader?> getFinalizedHeaderTask = finalizedBlockhash == Keccak.Zero 
            ? Task.FromResult<BlockHeader?>(null)
            : syncPeer.GetHeadBlockHeader(finalizedBlockhash, token);

        Task getHeaderTask = Task.WhenAll(getFinalizedHeaderTask, getFinalizedHeaderTask);
        Task firstToComplete = await Task.WhenAny(getHeaderTask, delayTask);

        if (firstToComplete == delayTask)
        {
            _syncPeerPool.ReportRefreshFailed(syncPeer, "timeout");
            return;
        }

        BlockHeader? headBlockHeader = null;
        BlockHeader? headParentBlockHeader = null;
        BlockHeader? finalizedBlockHeader = null;
        try
        {
            BlockHeader[] headAndParentHeaders = await getHeadParentHeaderTask;
            if (headAndParentHeaders.Length == 1 && headAndParentHeaders[0].Hash == headParentBlockhash)
            {
                headParentBlockHeader = headAndParentHeaders[0];
            }
            else if (headAndParentHeaders.Length == 2)
            {
                // Maybe the head is not the same as we expected. In that case, leave it as null
                if (headBlockhash == headAndParentHeaders[1].Hash)
                {
                    headBlockHeader = headAndParentHeaders[1];
                }
                headParentBlockHeader = headAndParentHeaders[0];
            }
            else if (headAndParentHeaders.Length > 0)
            {
                if (_logger.IsTrace) _logger.Trace($"PeerRefreshForFCU unexpected response length when fetching header: {headAndParentHeaders.Length}");
            }

            finalizedBlockHeader = await getFinalizedHeaderTask;
        }
        catch (TaskCanceledException exception)
        {
            if (_logger.IsTrace)
                _logger.Trace(
                    $"PeerRefreshForFCU canceled for node: {syncPeer.Node:c}{Environment.NewLine}{exception}");
            _syncPeerPool.ReportRefreshCancelled(syncPeer);
            throw;
        }
        catch (Exception exception)
        {
            if (_logger.IsTrace)
                _logger.Trace($"PeerRefreshForFCU failed for node: {syncPeer.Node:c}{Environment.NewLine}{exception}");
            _syncPeerPool.ReportRefreshFailed(syncPeer, "faulted");
            // TODO: how to know if exception is transient
            syncPeer.Disconnect(DisconnectReason.DisconnectRequested, "refresh peer info fault - faulted");
            return;
        }

        if (_logger.IsTrace)
        {
            _logger.Trace($"PeerRefreshForFCU received block info from {syncPeer.Node:c}");
            _logger.Trace($"PeerRefreshForFCU headHeader: {headBlockHeader}");
            _logger.Trace($"PeerRefreshForFCU headParentHeader: {headParentBlockHeader}");
            _logger.Trace($"PeerRefreshForFCU finalizedBlockHeader: {finalizedBlockHeader}");
        }

        if (finalizedBlockhash != Keccak.Zero && finalizedBlockHeader == null)
        {
            if (_logger.IsTrace)
                _logger.Trace($"PeerRefreshForFCU failed for node: {syncPeer.Node:c}{Environment.NewLine} - Finalized block header not found");
            _syncPeerPool.ReportRefreshFailed(syncPeer, "no finalized block header");
            return;
        }

        List<BlockHeader> headersToCheck = new [] {
            headBlockHeader,
            headParentBlockHeader,
            finalizedBlockHeader
        }.Where((header) => header != null).ToList();
        
        foreach (BlockHeader header in headersToCheck)
        {
            if (!HeaderValidator.ValidateHash(header))
            {
                if (_logger.IsTrace)
                    _logger.Trace($"PeerRefreshForFCU failed for node: {syncPeer.Node:c}{Environment.NewLine} Invalid block hash. Header: {header}");
                _syncPeerPool.ReportRefreshFailed(syncPeer, "invalid header hash");
                return;
            }
        }

        foreach (BlockHeader header in headersToCheck)
        {
            _syncPeerPool.UpdateSyncPeerHeadIfHeaderIsBetter(syncPeer, header);
        }

        _syncPeerPool.SignalPeersChanged();
    }
        
    public ValueTask DisposeAsync()
    {
        _refreshTimer.Dispose();
        return default;
    }
}

public interface IPeerRefresher
{
    void RefreshPeers(Keccak headBlockhash, Keccak headParentBlockhash, Keccak finalizedBlockhash);
}
