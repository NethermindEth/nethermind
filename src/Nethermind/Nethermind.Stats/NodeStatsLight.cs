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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    /// <summary>
    /// Initial version of Reputation calculation mostly based on EthereumJ impl
    /// </summary>
    public class NodeStatsLight : INodeStats
    {
        private readonly IStatsConfig _statsConfig;

        private long _pingPongLatencyEventCount;
        private decimal? _pingPongAverageLatency;
        private long _headersLatencyEventCount;
        private decimal? _headersAverageLatency;
        private long _bodiesLatencyEventCount;
        private decimal? _bodiesAverageLatency;

        private int[] _statCountersArray;
        private object _latencyLock = new object();

        private DisconnectReason? _lastLocalDisconnect;
        private DisconnectReason? _lastRemoteDisconnect;

        private DateTime? _lastDisconnectTime;
        private DateTime? _lastFailedConnectionTime;
        private static readonly Random Random = new Random();

        private static int _statsLength = Enum.GetValues(typeof(NodeStatsEventType)).Length;
        
        public NodeStatsLight(Node node, IStatsConfig statsConfig)
        {
            _statCountersArray = new int[_statsLength];
            _statsConfig = statsConfig;
            Node = node;
        }
        
        public long CurrentNodeReputation => CalculateCurrentReputation();

        public long CurrentPersistedNodeReputation { get; set; }

        public long NewPersistedNodeReputation => IsReputationPenalized() ? -100 : (CurrentPersistedNodeReputation + CalculateSessionReputation()) / 2;

        public bool IsTrustedPeer { get; set; }

        public P2PNodeDetails P2PNodeDetails { get; private set; }

        public EthNodeDetails EthNodeDetails { get; private set; }

        public CompatibilityValidationType? FailedCompatibilityValidation { get; set; }

        public Node Node { get; }

        public IEnumerable<NodeStatsEvent> EventHistory => Enumerable.Empty<NodeStatsEvent>();
        public IEnumerable<NodeLatencyStatsEvent> LatencyHistory => Enumerable.Empty<NodeLatencyStatsEvent>();

        private void Increment(NodeStatsEventType nodeStatsEventType)
        {
            lock (_statCountersArray)
            {
                _statCountersArray[(int)nodeStatsEventType]++;
            }
        }
        
        public void AddNodeStatsEvent(NodeStatsEventType nodeStatsEventType)
        {
            if (nodeStatsEventType == NodeStatsEventType.ConnectionFailed)
            {
                _lastFailedConnectionTime = DateTime.UtcNow;
            }

            Increment(nodeStatsEventType);
        }

        public void AddNodeStatsHandshakeEvent(ConnectionDirection connectionDirection)
        {
            Increment(NodeStatsEventType.HandshakeCompleted);
        }

        public void AddNodeStatsDisconnectEvent(DisconnectType disconnectType, DisconnectReason disconnectReason)
        {
            _lastDisconnectTime = DateTime.UtcNow;
            if (disconnectType == DisconnectType.Local)
            {
                _lastLocalDisconnect = disconnectReason;
            }
            else
            {
                _lastRemoteDisconnect = disconnectReason;
            }
            
            Increment(NodeStatsEventType.Disconnect);
        }

        public void AddNodeStatsP2PInitializedEvent(P2PNodeDetails nodeDetails)
        {
            P2PNodeDetails = nodeDetails;
            Increment(NodeStatsEventType.P2PInitialized);
        }

        public void AddNodeStatsEth62InitializedEvent(EthNodeDetails nodeDetails)
        {
            EthNodeDetails = nodeDetails;
            Increment(NodeStatsEventType.Eth62Initialized);
        }

        public void AddNodeStatsSyncEvent(NodeStatsEventType nodeStatsEventType)
        {
            Increment(nodeStatsEventType);
        }

        public bool DidEventHappen(NodeStatsEventType nodeStatsEventType)
        {
            lock (_statCountersArray)
            {
                return _statCountersArray[(int) nodeStatsEventType] > 0;
            }
        }

        public void AddLatencyCaptureEvent(NodeLatencyStatType latencyType, long milliseconds)
        {
            lock (_latencyLock)
            {
                switch (latencyType)
                {
                    case NodeLatencyStatType.P2PPingPong:
                        _pingPongAverageLatency = ((_pingPongLatencyEventCount * (_pingPongAverageLatency ?? 0)) + milliseconds) / (++_pingPongLatencyEventCount);
                        break;
                    case NodeLatencyStatType.BlockHeaders:
                        _headersAverageLatency = ((_headersLatencyEventCount * (_headersAverageLatency ?? 0)) + milliseconds) / (++_headersLatencyEventCount);
                        break;
                    case NodeLatencyStatType.BlockBodies:
                        _bodiesAverageLatency = ((_bodiesLatencyEventCount * (_bodiesAverageLatency ?? 0)) + milliseconds) / (++_bodiesLatencyEventCount);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(latencyType), latencyType, null);
                }
            }
        }

        public long? GetAverageLatency(NodeLatencyStatType latencyType)
        {
            switch (latencyType)
            {
                case NodeLatencyStatType.P2PPingPong:
                    return (long?)_pingPongAverageLatency;
                case NodeLatencyStatType.BlockHeaders:
                    return (long?)_headersAverageLatency;
                case NodeLatencyStatType.BlockBodies:
                    return (long?)_bodiesAverageLatency;
                default:
                    throw new ArgumentOutOfRangeException(nameof(latencyType), latencyType, null);
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

        private bool IsDelayedDueToDisconnect()
        {
            if (!_lastDisconnectTime.HasValue)
            {
                return false;
            }

            var timePassed = DateTime.UtcNow.Subtract(_lastDisconnectTime.Value).TotalMilliseconds;
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

            var timePassed = DateTime.UtcNow.Subtract(_lastFailedConnectionTime.Value).TotalMilliseconds;
            var failedConnectionDelay = GetFailedConnectionDelay();
            var result = timePassed < failedConnectionDelay;

            return result;
        }
        
        private int GetFailedConnectionDelay()
        {
            int failedConnectionFailed;
            lock (_statCountersArray)
            {
                failedConnectionFailed = _statCountersArray[(int)NodeStatsEventType.ConnectionFailed];    
            }
            
            if (failedConnectionFailed == 0)
            {
                return 100;
            }

            if (failedConnectionFailed > _statsConfig.FailedConnectionDelays.Length)
            {
                return _statsConfig.FailedConnectionDelays.Last();
            }

            return _statsConfig.FailedConnectionDelays[failedConnectionFailed - 1];
        }
        
        private int GetDisconnectDelay()
        {
            int disconnectCount;
            lock (_statCountersArray)
            {
                disconnectCount = _statCountersArray[(int)NodeStatsEventType.Disconnect];
            }

            if (disconnectCount == 0)
            {
                return 100;
            }

            if (disconnectCount > _statsConfig.DisconnectDelays.Length)
            {
                return _statsConfig.DisconnectDelays.Last();
            }

            return _statsConfig.DisconnectDelays[disconnectCount - 1];
        }


        private long CalculateCurrentReputation()
        {
            return IsReputationPenalized()
                ? -100
                : CurrentPersistedNodeReputation / 2 + CalculateSessionReputation() +
                  (Node.IsTrusted ? _statsConfig.PredefinedReputation : 0);
        }

        private bool HasDisconnectedOnce => _lastLocalDisconnect.HasValue || _lastRemoteDisconnect.HasValue;
        
        private long CalculateSessionReputation()
        {
            long discoveryReputation = 0;
            long rlpxReputation = 0;
            lock (_statCountersArray)
            {
                
                discoveryReputation += Math.Min(_statCountersArray[(int)NodeStatsEventType.DiscoveryPingIn], 10) * (_statCountersArray[(int)NodeStatsEventType.DiscoveryPingIn] == _statCountersArray[(int)NodeStatsEventType.DiscoveryPingOut] ? 2 : 1);
                discoveryReputation += Math.Min(_statCountersArray[(int)NodeStatsEventType.DiscoveryNeighboursIn], 10) * 2;

                
                rlpxReputation += Math.Min(_statCountersArray[(int)NodeStatsEventType.P2PPingIn], 10) * (_statCountersArray[(int)NodeStatsEventType.P2PPingIn] == _statCountersArray[(int)NodeStatsEventType.P2PPingOut] ? 2 : 1);
                rlpxReputation += _statCountersArray[(int)NodeStatsEventType.HandshakeCompleted] > 0 ? 10 : 0;
                rlpxReputation += _statCountersArray[(int)NodeStatsEventType.P2PInitialized] > 0 ? 10 : 0;
                rlpxReputation += _statCountersArray[(int)NodeStatsEventType.Eth62Initialized] > 0 ? 20 : 0;
                rlpxReputation += _statCountersArray[(int)NodeStatsEventType.SyncStarted] > 0 ? 1000 : 0;
            }

            if (HasDisconnectedOnce)
            {
                if (_lastLocalDisconnect == DisconnectReason.Other || _lastRemoteDisconnect == DisconnectReason.Other)
                {
                    rlpxReputation = (long)(rlpxReputation * 0.3);
                }
                else if(_lastLocalDisconnect != DisconnectReason.DisconnectRequested)
                {
                    if (_lastRemoteDisconnect == DisconnectReason.TooManyPeers)
                    {
                        rlpxReputation = (long) (rlpxReputation * 0.3);
                    }
                    else if (_lastRemoteDisconnect != DisconnectReason.DisconnectRequested)
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
            if (!HasDisconnectedOnce)
            {
                return false;
            }

            if (_lastLocalDisconnect.HasValue)
            {               
                if (_statsConfig.PenalizedReputationLocalDisconnectReasons.Contains(_lastLocalDisconnect.Value))
                {
                    return true;
                }
            }

            if (!_lastRemoteDisconnect.HasValue)
            {
                return false;
            }

            if (_statsConfig.PenalizedReputationRemoteDisconnectReasons.Contains(_lastRemoteDisconnect.Value))
            {
                if (_lastRemoteDisconnect == DisconnectReason.TooManyPeers || _lastRemoteDisconnect == DisconnectReason.AlreadyConnected)
                {
                    var timeFromLastDisconnect = DateTime.UtcNow.Subtract(_lastDisconnectTime ?? DateTime.MinValue).TotalMilliseconds;
                    return timeFromLastDisconnect < _statsConfig.PenalizedReputationTooManyPeersTimeout;
                }

                return true;
            }

            return false;
        }
    }
}
