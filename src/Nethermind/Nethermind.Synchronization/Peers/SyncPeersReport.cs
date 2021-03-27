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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.Logging;
using Nethermind.Stats;

namespace Nethermind.Synchronization.Peers
{
    /// <summary>
    /// This class is responsible for logging / reporting lists of peers
    /// </summary>
    internal class SyncPeersReport
    {
        private StringBuilder _stringBuilder = new();
        private int _currentInitializedPeerCount;

        private readonly ISyncPeerPool _peerPool;
        private readonly INodeStatsManager _stats;
        private readonly ILogger _logger;

        public SyncPeersReport(ISyncPeerPool peerPool, INodeStatsManager statsManager, ILogManager logManager)
        {
            lock (_writeLock)
            {
                _peerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
                _stats = statsManager ?? throw new ArgumentNullException(nameof(statsManager));
                _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            }
        }

        private object _writeLock = new();

        private IEnumerable<PeerInfo> OrderedPeers => _peerPool.InitializedPeers
            .OrderByDescending(p => p.SyncPeer?.HeadNumber)
            .ThenByDescending(p => p.SyncPeer?.Node?.ClientId.StartsWith("Nethermind") ?? false)
            .ThenByDescending(p => p.SyncPeer?.Node?.ClientId).ThenBy(p => p.SyncPeer?.Node?.Host);

        public void WriteFullReport()
        {
            lock (_writeLock)
            {
                if (!_logger.IsInfo)
                {
                    return;
                }

                RememberState(out bool _);
                _stringBuilder.Append($"Sync peers - Initialized: {_currentInitializedPeerCount} | All: {_peerPool.PeerCount} | Max: {_peerPool.PeerMaxCount}");
                foreach (PeerInfo peerInfo in OrderedPeers)
                {
                    _stringBuilder.AppendLine();
                    AddPeerInfo(peerInfo);
                }

                _logger.Info(_stringBuilder.ToString());
                _stringBuilder.Clear();
            }
        }

        public void WriteShortReport()
        {
            lock (_writeLock)
            {
                if (!_logger.IsInfo)
                {
                    return;
                }

                RememberState(out bool changed);
                if (!changed)
                {
                    return;
                }
                
                _stringBuilder.Append($"Sync peers {_currentInitializedPeerCount}({_peerPool.PeerCount})/{_peerPool.PeerMaxCount}");
                foreach (PeerInfo peerInfo in OrderedPeers.Where(p => !p.CanBeAllocated(AllocationContexts.All)))
                {
                    _stringBuilder.AppendLine();
                    AddPeerInfo(peerInfo);
                }
                
                _logger.Info(_stringBuilder.ToString());
                _stringBuilder.Clear();
            }
        }

        private void AddPeerInfo(PeerInfo peerInfo)
        {
            _stringBuilder.Append($"   {peerInfo}[{_stats.GetOrAdd(peerInfo.SyncPeer.Node).GetAverageTransferSpeed(TransferSpeedType.Latency) ?? 0}|{_stats.GetOrAdd(peerInfo.SyncPeer.Node).GetAverageTransferSpeed(TransferSpeedType.Headers) ?? 0}|{_stats.GetOrAdd(peerInfo.SyncPeer.Node).GetAverageTransferSpeed(TransferSpeedType.Bodies) ?? 0}|{_stats.GetOrAdd(peerInfo.SyncPeer.Node).GetAverageTransferSpeed(TransferSpeedType.Receipts) ?? 0}|{_stats.GetOrAdd(peerInfo.SyncPeer.Node).GetAverageTransferSpeed(TransferSpeedType.NodeData) ?? 0}]");
        }

        private void RememberState(out bool initializedCountChanged)
        {
            int initializedPeerCount = _peerPool.InitializedPeersCount;
            initializedCountChanged = initializedPeerCount != _currentInitializedPeerCount;
            _currentInitializedPeerCount = initializedPeerCount;
        }
    }
}
