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
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Merge.Plugin.Synchronization;

public class PeerRefresher : IPeerRefresher, IAsyncDisposable
{
    private readonly ISyncPeerPool _syncPeerPool;
    private static readonly TimeSpan _minRefreshDelay = TimeSpan.FromSeconds(10);
    private DateTime _lastRefresh = DateTime.MinValue;
    private (Keccak, Keccak, Keccak) _lastBlockhashes = (Keccak.Zero, Keccak.Zero, Keccak.Zero);
    private readonly ITimer _refreshTimer;

    public PeerRefresher(ISyncPeerPool syncPeerPool, ITimerFactory timerFactory)
    {
        _refreshTimer = timerFactory.CreateTimer(_minRefreshDelay);
        _refreshTimer.Elapsed += TimerOnElapsed;
        _refreshTimer.AutoReset = false;
        _syncPeerPool = syncPeerPool;
    }

    public void RefreshPeers(Keccak headBlockhash, Keccak parentHeadBlockhash, Keccak finalizedBlockhash)
    {
        _lastBlockhashes = (headBlockhash, parentHeadBlockhash, finalizedBlockhash);
        TimeSpan timePassed = DateTime.Now - _lastRefresh;
        if (timePassed > _minRefreshDelay)
        {
            Refresh(headBlockhash, parentHeadBlockhash, finalizedBlockhash);
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
    
    private void Refresh(Keccak headBlockhash, Keccak parentHeadBlockhash, Keccak finalizedBlockhash)
    {
        _lastRefresh = DateTime.Now;
        foreach (PeerInfo peer in _syncPeerPool.AllPeers)
        {
            _syncPeerPool.RefreshTotalDifficultyForFcu(peer.SyncPeer, headBlockhash, parentHeadBlockhash, finalizedBlockhash);
        }
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
