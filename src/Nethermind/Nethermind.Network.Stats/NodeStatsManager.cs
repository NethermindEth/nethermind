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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;
using Nethermind.Core.Caching;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    public class NodeStatsManager : INodeStatsManager, IDisposable
    {
        private class NodeComparer : IEqualityComparer<Node>
        {
            public bool Equals(Node x, Node y)
            {
                if (ReferenceEquals(x, null))
                {
                    return ReferenceEquals(y, null);
                }

                if (ReferenceEquals(y, null))
                {
                    return false;
                }

                return x.Id == y.Id;
            }

            public int GetHashCode(Node obj)
            {
                return obj?.GetHashCode() ?? 0;
            }
        }
        
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<Node, INodeStats> _nodeStats = new ConcurrentDictionary<Node, INodeStats>(new NodeComparer());
        private readonly ITimer _cleanupTimer;
        private readonly int _maxCount;

        public NodeStatsManager(ITimerFactory timerFactory, ILogManager logManager, int maxCount = 10000)
        {
            _maxCount = maxCount;
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            _cleanupTimer = timerFactory.CreateTimer(TimeSpan.FromMinutes(10));
            _cleanupTimer.Elapsed += CleanupTimerOnElapsed;
            _cleanupTimer.Start();
        }

        private void CleanupTimerOnElapsed(object sender, EventArgs e)
        {
            int deleteCount = _nodeStats.Count - _maxCount;

            if (deleteCount > 0)
            {
                IEnumerable<Node> toDelete = _nodeStats
                    .OrderBy(n => n.Value.CurrentNodeReputation)
                    .Select(n => n.Key)
                    .Take(_nodeStats.Count - _maxCount);

                int i = 0;
                foreach (Node node in toDelete)
                {
                    _nodeStats.TryRemove(node, out _);
                    i++;
                }
                
                if (_logger.IsDebug) _logger.Debug($"Removed {i} node stats.");
            }
        }

        private INodeStats AddStats(Node node)
        {
            return new NodeStatsLight(node);
        }
        
        public INodeStats GetOrAdd(Node node)
        {
            if (node == null)
            {
                return null;
            }

            // to avoid allocations
            if (_nodeStats.TryGetValue(node, out INodeStats stats))
            {
                return stats;
            }
            
            return _nodeStats.GetOrAdd(node, AddStats);
        }

        public void ReportHandshakeEvent(Node node, ConnectionDirection direction)
        {
            INodeStats stats = GetOrAdd(node);
            stats.AddNodeStatsHandshakeEvent(direction);
        }

        public void ReportSyncEvent(Node node, NodeStatsEventType nodeStatsEvent)
        {
            INodeStats stats = GetOrAdd(node);
            stats.AddNodeStatsSyncEvent(nodeStatsEvent);
        }
        
        public void ReportEvent(Node node, NodeStatsEventType eventType)
        {
            INodeStats stats = GetOrAdd(node);
            stats.AddNodeStatsEvent(eventType);
        }

        public (bool Result, NodeStatsEventType? DelayReason) IsConnectionDelayed(Node node)
        {
            INodeStats stats = GetOrAdd(node);
            return stats.IsConnectionDelayed();
        }

        public CompatibilityValidationType? FindCompatibilityValidationResult(Node node)
        {
            INodeStats stats = GetOrAdd(node);
            return stats.FailedCompatibilityValidation;
        }

        public long GetCurrentReputation(Node node)
        {
            INodeStats stats = GetOrAdd(node);
            return stats.CurrentNodeReputation;
        }

        public void ReportP2PInitializationEvent(Node node, P2PNodeDetails p2PNodeDetails)
        {
            INodeStats stats = GetOrAdd(node);
            stats.AddNodeStatsP2PInitializedEvent(p2PNodeDetails);
        }

        public void ReportSyncPeerInitializeEvent(string protocol, Node node, SyncPeerNodeDetails syncPeerNodeDetails)
        {
            INodeStats stats = GetOrAdd(node);
            if (protocol == "eth")
                stats.AddNodeStatsEth62InitializedEvent(syncPeerNodeDetails);
            else if (protocol == "les")
                stats.AddNodeStatsLesInitializedEvent(syncPeerNodeDetails);
            else
                throw new ArgumentException($"Unknown protocol: {protocol}");
        }

        public void ReportFailedValidation(Node node, CompatibilityValidationType validationType)
        {
            INodeStats stats = GetOrAdd(node);
            stats.FailedCompatibilityValidation = validationType;
        }

        public void ReportDisconnect(Node node, DisconnectType disconnectType, DisconnectReason disconnectReason)
        {
            INodeStats stats = GetOrAdd(node);
            stats.AddNodeStatsDisconnectEvent(disconnectType, disconnectReason);
        }

        public long GetNewPersistedReputation(Node node)
        {
            INodeStats stats = GetOrAdd(node);
            return stats.NewPersistedNodeReputation;
        }

        public long GetCurrentPersistedReputation(Node node)
        {
            INodeStats stats = GetOrAdd(node);
            return stats.CurrentPersistedNodeReputation;
        }

        public bool HasFailedValidation(Node node)
        {
            INodeStats stats = GetOrAdd(node);
            return stats.FailedCompatibilityValidation != null;
        }

        public void ReportTransferSpeedEvent(Node node, TransferSpeedType type, long value)
        {
            INodeStats stats = GetOrAdd(node);
            stats.AddTransferSpeedCaptureEvent(type, value);
        }

        public void Dispose()
        {
            _cleanupTimer.Dispose();
        }
    }
}
