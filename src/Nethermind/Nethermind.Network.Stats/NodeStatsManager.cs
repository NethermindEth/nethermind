// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    public class NodeStatsManager : INodeStatsManager, IDisposable
    {
        private class NodeComparer : IEqualityComparer<Node>
        {
            public bool Equals(Node x, Node y) => ReferenceEquals(x, y) || x.Id == y.Id;
            public int GetHashCode(Node obj) => obj.GetHashCode();
        }

        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<Node, INodeStats> _nodeStats = new(new NodeComparer());
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
            _cleanupTimer.Stop();

            int deleteCount = _nodeStats.Count - _maxCount;

            if (deleteCount > 0)
            {
                DateTime utcNow = DateTime.UtcNow;
                IEnumerable<Node> toDelete = _nodeStats
                    .OrderBy(n => n.Value.CurrentNodeReputation(utcNow))
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

            _cleanupTimer.Start();
        }

        private INodeStats AddStats(Node node)
        {
            return new NodeStatsLight(node);
        }

        public INodeStats GetOrAdd(Node node)
        {
            if (node is null)
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
            return stats.IsConnectionDelayed(DateTime.UtcNow);
        }

        public CompatibilityValidationType? FindCompatibilityValidationResult(Node node)
        {
            INodeStats stats = GetOrAdd(node);
            return stats.FailedCompatibilityValidation;
        }

        public long GetCurrentReputation(Node node)
        {
            INodeStats stats = GetOrAdd(node);
            return stats.CurrentNodeReputation(DateTime.UtcNow);
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

        public void ReportDisconnect(Node node, DisconnectType disconnectType, EthDisconnectReason ethDisconnectReason)
        {
            INodeStats stats = GetOrAdd(node);
            stats.AddNodeStatsDisconnectEvent(disconnectType, ethDisconnectReason);
        }

        public long GetNewPersistedReputation(Node node)
        {
            INodeStats stats = GetOrAdd(node);
            return stats.NewPersistedNodeReputation(DateTime.UtcNow);
        }

        public long GetCurrentPersistedReputation(Node node)
        {
            INodeStats stats = GetOrAdd(node);
            return stats.CurrentPersistedNodeReputation;
        }

        public bool HasFailedValidation(Node node)
        {
            INodeStats stats = GetOrAdd(node);
            return stats.FailedCompatibilityValidation is not null;
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
