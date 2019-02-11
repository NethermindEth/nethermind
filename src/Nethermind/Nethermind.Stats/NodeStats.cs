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
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.Stats.Model;

[assembly: InternalsVisibleTo("Nethermind.Benchmarks")]
[assembly: InternalsVisibleTo("Nethermind.Network.Test")]
namespace Nethermind.Stats
{
    /// <summary>
    /// Initial version of Reputation calculation mostly based on EthereumJ impl
    /// </summary>
    internal class NodeStats : INodeStats
    {
        private readonly IStatsConfig _statsConfig;
        private ConcurrentDictionary<NodeLatencyStatType, ConcurrentBag<NodeLatencyStatsEvent>> _latencyStatsLog;
        private ConcurrentDictionary<NodeLatencyStatType, (long EventCount, decimal? Latency)> _latencyStats;
        private ConcurrentDictionary<NodeLatencyStatType, object> _latencyStatsLocks;
        private ConcurrentDictionary<NodeStatsEventType, AtomicLong> _statCounters;
        private ConcurrentDictionary<DisconnectType, (DisconnectReason DisconnectReason, DateTime DisconnectTime)> _lastDisconnects;        
        private ConcurrentBag<NodeStatsEvent> _eventHistory;
        private DateTime? _lastDisconnectTime;
        private DateTime? _lastFailedConnectionTime;
        private static readonly Random Random = new Random();
        
        public NodeStats(Node node, IStatsConfig statsConfig)
        {
            Node = node;
            _statsConfig = statsConfig;
            Initialize();
        }

        public IEnumerable<NodeStatsEvent> EventHistory => _eventHistory.ToArray();
        public IEnumerable<NodeLatencyStatsEvent> LatencyHistory => _latencyStatsLog.SelectMany(x => x.Value).ToArray();
        public IDictionary<DisconnectType, (DisconnectReason DisconnectReason, DateTime DisconnectTime)> LastDisconnects => _lastDisconnects.ToDictionary(x => x.Key, y => y.Value);
        
        public void AddNodeStatsEvent(NodeStatsEventType nodeStatsEventType)
        {
//            if (nodeStatsEventType == NodeStatsEventType.ConnectionEstablished)
//            {
//                //reset failed connection counter in case connection was established
//                _statCounters[NodeStatsEventType.ConnectionFailed] = new AtomicLong();
//            }

            if (nodeStatsEventType == NodeStatsEventType.ConnectionFailed)
            {
                _lastFailedConnectionTime = DateTime.Now;
            }
            _statCounters[nodeStatsEventType].Increment();
            CaptureEvent(nodeStatsEventType);
        }

        public void AddNodeStatsHandshakeEvent(ConnectionDirection connectionDirection)
        {
            _statCounters[NodeStatsEventType.HandshakeCompleted].Increment();
            CaptureEvent(NodeStatsEventType.HandshakeCompleted, null, null, null, connectionDirection);
        }

        public void AddNodeStatsDisconnectEvent(DisconnectType disconnectType, DisconnectReason disconnectReason)
        {
            DateTime time = DateTime.Now;
            _lastDisconnectTime = time;
            _lastDisconnects[disconnectType] = (disconnectReason, time);
            _statCounters[NodeStatsEventType.Disconnect].Increment();
            CaptureEvent(NodeStatsEventType.Disconnect, null, null, new DisconnectDetails
            {
                DisconnectReason = disconnectReason,
                DisconnectType = disconnectType
            });
        }

        public void AddNodeStatsP2PInitializedEvent(P2PNodeDetails nodeDetails)
        {
            P2PNodeDetails = nodeDetails;
            _statCounters[NodeStatsEventType.P2PInitialized].Increment();
            CaptureEvent(NodeStatsEventType.P2PInitialized, nodeDetails);
        }

        public void AddNodeStatsEth62InitializedEvent(EthNodeDetails nodeDetails)
        {
            EthNodeDetails = nodeDetails;
            _statCounters[NodeStatsEventType.Eth62Initialized].Increment();
            CaptureEvent(NodeStatsEventType.Eth62Initialized, null, nodeDetails);
        }

        public void AddNodeStatsSyncEvent(NodeStatsEventType nodeStatsEventType, SyncNodeDetails syncDetails)
        {
            if (SyncNodeDetails == null)
            {
                SyncNodeDetails = syncDetails;
            }
            else
            {
                if (syncDetails.NodeBestBlockNumber.HasValue) SyncNodeDetails.NodeBestBlockNumber = syncDetails.NodeBestBlockNumber;
                if (syncDetails.OurBestBlockNumber.HasValue) SyncNodeDetails.OurBestBlockNumber = syncDetails.OurBestBlockNumber;
            }
            _statCounters[nodeStatsEventType].Increment();
            CaptureEvent(nodeStatsEventType, null, null, null, null, syncDetails);
        }

        public bool DidEventHappen(NodeStatsEventType nodeStatsEventType)
        {
            return _statCounters[nodeStatsEventType].Value > 0;
        }

        public void AddLatencyCaptureEvent(NodeLatencyStatType latencyType, long miliseconds)
        {
            lock (_latencyStatsLocks[latencyType])
            {
                if (_statsConfig.CaptureNodeLatencyStatsEventHistory)
                {
                    var collection = _latencyStatsLog[latencyType];
                    collection.Add(new NodeLatencyStatsEvent
                    {
                        StatType = latencyType,
                        CaptureTime = DateTime.Now,
                        Latency = miliseconds
                    });
                }

                var latencyInfo = _latencyStats[latencyType];
                var latency = ((latencyInfo.EventCount * (latencyInfo.Latency ?? 0)) + miliseconds) / (latencyInfo.EventCount + 1);
                _latencyStats[latencyType] = (latencyInfo.EventCount + 1, latency);
            }
        }

        public long? GetAverageLatency(NodeLatencyStatType latencyType)
        {
            lock (_latencyStatsLocks[latencyType])
            {
                var lat = _latencyStats[latencyType].Latency;
                return lat != null ? (long)lat : (long?)null;
            }
        }

        public (bool Result, NodeStatsEventType? DelayReason) IsConnectionDelayed()
        {
            if (IsDelayedDueToDisconnect())
            {
                return (true, NodeStatsEventType.Disconnect);
            }

            if (IsDelayedDueToFailedConnection())
            {
                return (true, NodeStatsEventType.ConnectionFailed);
            }
            
            return (false, null);
        }

        public long CurrentNodeReputation => CalculateCurrentReputation();

        public long CurrentPersistedNodeReputation { get; set; }

        public long NewPersistedNodeReputation => IsReputationPenalized() ? -100 : (CurrentPersistedNodeReputation + CalculateSessionReputation()) / 2;

        public P2PNodeDetails P2PNodeDetails { get; private set; }

        public EthNodeDetails EthNodeDetails { get; private set; }

        public CompatibilityValidationType? FailedCompatibilityValidation { get; set; }

        public SyncNodeDetails SyncNodeDetails { get; private set; }

        public Node Node { get; }

        private bool IsDelayedDueToDisconnect()
        {
            if (!_lastDisconnectTime.HasValue)
            {
                return false;
            }

            var timePassed = DateTime.Now.Subtract(_lastDisconnectTime.Value).TotalMilliseconds;
            var disconnectDelay = GetDisconnectDelay();
            if (disconnectDelay <= 500)
            {
                //randomize early disconnect delay - for private networks
                lock (Random)
                {
                    var randomizedDelay = Random.Next(disconnectDelay);
                    disconnectDelay = randomizedDelay < 10 ? randomizedDelay + 10 : randomizedDelay;
                }
            }
            var result = timePassed < disconnectDelay;
            return result;
        }

        private bool IsDelayedDueToFailedConnection()
        {
            if (!_lastFailedConnectionTime.HasValue)
            {
                return false;
            }

            var timePassed = DateTime.Now.Subtract(_lastFailedConnectionTime.Value).TotalMilliseconds;
            var failedConnectionDelay = GetFailedConnectionDelay();
            var result = timePassed < failedConnectionDelay;

            return result;
        }
        
        private int GetFailedConnectionDelay()
        {
            var failedConnectionsCount = _statCounters[NodeStatsEventType.ConnectionFailed].Value;
            if (failedConnectionsCount == 0)
            {
                return 100;
            }

            return _statsConfig.FailedConnectionDelays[Math.Min(failedConnectionsCount, _statsConfig.FailedConnectionDelays.Length) - 1];
        }
        
        private int GetDisconnectDelay()
        {
            var disconnectCount = _statCounters[NodeStatsEventType.Disconnect].Value;
            if (disconnectCount == 0)
            {
                return 100;
            }

            return _statsConfig.DisconnectDelays[Math.Min(disconnectCount, _statsConfig.DisconnectDelays.Length) - 1];
        }
        
        private void CaptureEvent(NodeStatsEventType eventType, P2PNodeDetails p2PNodeDetails = null, EthNodeDetails ethNodeDetails = null, DisconnectDetails disconnectDetails = null, ConnectionDirection? connectionDirection = null, SyncNodeDetails syncNodeDetails = null)
        {
            if (!_statsConfig.CaptureNodeStatsEventHistory)
            {
                return;
            }

            if (eventType.ToString().Contains("Discovery") || new []{NodeStatsEventType.P2PPingIn, NodeStatsEventType.P2PPingOut}.Contains(eventType))
            {
                return;
            }

            _eventHistory.Add(new NodeStatsEvent
            {
                EventType = eventType,
                EventDate = DateTime.Now,
                P2PNodeDetails = p2PNodeDetails,
                EthNodeDetails = ethNodeDetails,
                DisconnectDetails = disconnectDetails,
                ConnectionDirection = connectionDirection,
                SyncNodeDetails = syncNodeDetails
            });
        }

        private long CalculateCurrentReputation()
        {
            return IsReputationPenalized()
                ? -100
                : CurrentPersistedNodeReputation / 2 + CalculateSessionReputation() +
                  (Node.IsTrusted ? _statsConfig.PredefinedReputation : 0);
        }

        private long CalculateSessionReputation()
        {
            long discoveryReputation = 0;
            discoveryReputation += Math.Min(_statCounters[NodeStatsEventType.DiscoveryPingIn].Value, 10) * (_statCounters[NodeStatsEventType.DiscoveryPingIn].Value == _statCounters[NodeStatsEventType.DiscoveryPingOut].Value ? 2 : 1);
            discoveryReputation += Math.Min(_statCounters[NodeStatsEventType.DiscoveryNeighboursIn].Value, 10) * 2;

            long rlpxReputation = 0;
            rlpxReputation += Math.Min(_statCounters[NodeStatsEventType.P2PPingIn].Value, 10) * (_statCounters[NodeStatsEventType.P2PPingIn].Value == _statCounters[NodeStatsEventType.P2PPingOut].Value ? 2 : 1);
            rlpxReputation += _statCounters[NodeStatsEventType.HandshakeCompleted].Value > 0 ? 10 : 0;
            rlpxReputation += _statCounters[NodeStatsEventType.P2PInitialized].Value > 0 ? 10 : 0;
            rlpxReputation += _statCounters[NodeStatsEventType.Eth62Initialized].Value > 0 ? 20 : 0;
            rlpxReputation += _statCounters[NodeStatsEventType.SyncStarted].Value > 0 ? 1000 : 0;

            if (_lastDisconnects.Count != 0)
            {
                var localDisconnectReason = _lastDisconnects.ContainsKey(DisconnectType.Local) ? _lastDisconnects[DisconnectType.Local].DisconnectReason : (DisconnectReason?)null;
                var remoteDisconnectReason = _lastDisconnects.ContainsKey(DisconnectType.Remote) ? _lastDisconnects[DisconnectType.Remote].DisconnectReason : (DisconnectReason?)null;
                if (localDisconnectReason == DisconnectReason.Other || remoteDisconnectReason == DisconnectReason.Other)
                {
                    rlpxReputation = (long)(rlpxReputation * 0.3);
                }
                else if(localDisconnectReason != DisconnectReason.DisconnectRequested)
                {
                    if (remoteDisconnectReason == DisconnectReason.TooManyPeers)
                    {
                        rlpxReputation = (long) (rlpxReputation * 0.3);
                    }
                    else if (remoteDisconnectReason != DisconnectReason.DisconnectRequested)
                    {
                        rlpxReputation = (long) (rlpxReputation * 0.2);
                    }
                }
            }

            if (DidEventHappen(NodeStatsEventType.ConnectionFailed))
            {
                rlpxReputation = (long) (rlpxReputation * 0.2);
            }

            if (DidEventHappen(NodeStatsEventType.SyncInitFailed))
            {
                rlpxReputation = (long)(rlpxReputation * 0.3);
            }

            if (DidEventHappen(NodeStatsEventType.SyncFailed))
            {
                rlpxReputation = (long)(rlpxReputation * 0.4);
            }

            return discoveryReputation + 100 * rlpxReputation;
        }

        private bool IsReputationPenalized()
        {
            if (_lastDisconnects.Count == 0)
            {
                return false;
            }

            var lastOverallDisconnectTime = DateTime.MinValue;

            if (_lastDisconnects.ContainsKey(DisconnectType.Local))
            {
                var localDisconnect = _lastDisconnects[DisconnectType.Local];               
                if (_statsConfig.PenalizedReputationLocalDisconnectReasons.Contains(localDisconnect.DisconnectReason))
                {
                    return true;
                }
                lastOverallDisconnectTime = localDisconnect.DisconnectTime;
            }

            if (!_lastDisconnects.ContainsKey(DisconnectType.Remote))
            {
                return false;
            }

            var remoteDisconnect = _lastDisconnects[DisconnectType.Remote];
            if (remoteDisconnect.DisconnectTime > lastOverallDisconnectTime)
            {
                lastOverallDisconnectTime = remoteDisconnect.DisconnectTime;
            }
            if (_statsConfig.PenalizedReputationRemoteDisconnectReasons.Contains(remoteDisconnect.DisconnectReason))
            {
                if (new[] {DisconnectReason.AlreadyConnected, DisconnectReason.TooManyPeers}.Contains(remoteDisconnect.DisconnectReason))
                {
                    var timeFromLastDisconnect = DateTime.Now.Subtract(lastOverallDisconnectTime).TotalMilliseconds;
                    return timeFromLastDisconnect < _statsConfig.PenalizedReputationTooManyPeersTimeout;
                }

                return true;
            }

            return false;
        }

        private void Initialize()
        {
            _eventHistory = new ConcurrentBag<NodeStatsEvent>();
            
            _statCounters = new ConcurrentDictionary<NodeStatsEventType, AtomicLong>();
            foreach (NodeStatsEventType statType in Enum.GetValues(typeof(NodeStatsEventType)))
            {
                _statCounters[statType] = new AtomicLong();
            }

            _latencyStatsLog = new ConcurrentDictionary<NodeLatencyStatType, ConcurrentBag<NodeLatencyStatsEvent>>();
            _latencyStats = new ConcurrentDictionary<NodeLatencyStatType, (long EventCount, decimal? Latency)>();
            _latencyStatsLocks = new ConcurrentDictionary<NodeLatencyStatType, object>();
            foreach (NodeLatencyStatType statType in Enum.GetValues(typeof(NodeLatencyStatType)))
            {
                _latencyStatsLog[statType] = new ConcurrentBag<NodeLatencyStatsEvent>();
                _latencyStats[statType] = (0, null);
                _latencyStatsLocks[statType] = new object();
            }

            _lastDisconnects = new ConcurrentDictionary<DisconnectType, (DisconnectReason DisconnectReason, DateTime DisconnectTime)>();
        }
    }
}
