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
using System.Linq;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    /// <summary>
    /// Initial version of Reputation calculation mostly based on EthereumJ impl
    /// </summary>
    public class NodeStatsLight : INodeStats
    {
        private readonly StatsParameters _statsParameters;

        private long _headersTransferSpeedEventCount;
        private long _bodiesTransferSpeedEventCount;
        private long _receiptsTransferSpeedEventCount;
        private long _nodesTransferSpeedEventCount;
        private long _latencyEventCount;
        
        private decimal? _averageNodesTransferSpeed;
        private decimal? _averageHeadersTransferSpeed;
        private decimal? _averageBodiesTransferSpeed;
        private decimal? _averageReceiptsTransferSpeed;
        private decimal? _averageLatency;

        private int[] _statCountersArray;
        private object _speedLock = new object();

        private DisconnectReason? _lastLocalDisconnect;
        private DisconnectReason? _lastRemoteDisconnect;

        private DateTime? _lastDisconnectTime;
        private DateTime? _lastFailedConnectionTime;
        private static readonly Random Random = new Random();

        private static int _statsLength = Enum.GetValues(typeof(NodeStatsEventType)).Length;
        
        public NodeStatsLight(Node node)
        {
            _statCountersArray = new int[_statsLength];
            _statsParameters = StatsParameters.Instance;
            Node = node;
        }
        
        public long CurrentNodeReputation => CalculateCurrentReputation();

        public long CurrentPersistedNodeReputation { get; set; }

        public long NewPersistedNodeReputation => IsReputationPenalized() ? -100 : (CurrentPersistedNodeReputation + CalculateSessionReputation()) / 2;

        public P2PNodeDetails P2PNodeDetails { get; private set; }

        public SyncPeerNodeDetails EthNodeDetails { get; private set; }

        public SyncPeerNodeDetails LesNodeDetails { get; private set; }

        public CompatibilityValidationType? FailedCompatibilityValidation { get; set; }

        public Node Node { get; }

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

        public void AddNodeStatsEth62InitializedEvent(SyncPeerNodeDetails nodeDetails)
        {
            EthNodeDetails = nodeDetails;
            Increment(NodeStatsEventType.Eth62Initialized);
        }
        public void AddNodeStatsLesInitializedEvent(SyncPeerNodeDetails nodeDetails)
        {
            LesNodeDetails = nodeDetails;
            Increment(NodeStatsEventType.LesInitialized);
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

        public void AddTransferSpeedCaptureEvent(TransferSpeedType transferSpeedType, long bytesPerMillisecond)
        {
            lock (_speedLock)
            {
                switch (transferSpeedType)
                {
                    case TransferSpeedType.Latency:
                        _averageLatency = ((_latencyEventCount * (_averageLatency ?? 0)) + bytesPerMillisecond) / (++_latencyEventCount);
                        break;
                    case TransferSpeedType.NodeData:
                        _averageNodesTransferSpeed = ((_nodesTransferSpeedEventCount * (_averageNodesTransferSpeed ?? 0)) + bytesPerMillisecond) / (++_nodesTransferSpeedEventCount);
                        break;
                    case TransferSpeedType.Headers:
                        _averageHeadersTransferSpeed = ((_headersTransferSpeedEventCount * (_averageHeadersTransferSpeed ?? 0)) + bytesPerMillisecond) / (++_headersTransferSpeedEventCount);
                        break;
                    case TransferSpeedType.Bodies:
                        _averageBodiesTransferSpeed = ((_bodiesTransferSpeedEventCount * (_averageBodiesTransferSpeed ?? 0)) + bytesPerMillisecond) / (++_bodiesTransferSpeedEventCount);
                        break;
                    case TransferSpeedType.Receipts:
                        _averageReceiptsTransferSpeed = ((_receiptsTransferSpeedEventCount * (_averageReceiptsTransferSpeed ?? 0)) + bytesPerMillisecond) / (++_receiptsTransferSpeedEventCount);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(transferSpeedType), transferSpeedType, null);
                }
            }
        }

        public long? GetAverageTransferSpeed(TransferSpeedType transferSpeedType)
        {
            return (long?)(transferSpeedType switch
            {
                TransferSpeedType.Latency => _averageLatency,
                TransferSpeedType.NodeData => _averageNodesTransferSpeed,
                TransferSpeedType.Headers => _averageHeadersTransferSpeed,
                TransferSpeedType.Bodies => _averageBodiesTransferSpeed,
                TransferSpeedType.Receipts => _averageReceiptsTransferSpeed,
                _ => throw new ArgumentOutOfRangeException()
            });
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

            double timePassed = DateTime.UtcNow.Subtract(_lastDisconnectTime.Value).TotalMilliseconds;
            int disconnectDelay = GetDisconnectDelay();
            if (disconnectDelay <= 500)
            {
                //randomize early disconnect delay - for private networks
                lock (Random)
                {
                    int randomizedDelay = Random.Next(disconnectDelay);
                    disconnectDelay = randomizedDelay < 10 ? randomizedDelay + 10 : randomizedDelay;
                }
            }
            
            
            bool result = timePassed < disconnectDelay;
            return result;
        }

        private bool IsDelayedDueToFailedConnection()
        {
            if (!_lastFailedConnectionTime.HasValue)
            {
                return false;
            }

            double timePassed = DateTime.UtcNow.Subtract(_lastFailedConnectionTime.Value).TotalMilliseconds;
            int failedConnectionDelay = GetFailedConnectionDelay();
            bool result = timePassed < failedConnectionDelay;

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

            if (failedConnectionFailed > _statsParameters.FailedConnectionDelays.Length)
            {
                return _statsParameters.FailedConnectionDelays.Last();
            }

            return _statsParameters.FailedConnectionDelays[failedConnectionFailed - 1];
        }
        
        private int GetDisconnectDelay()
        {
            int disconnectDelay;
            int disconnectCount;
            lock (_statCountersArray)
            {
                disconnectCount = _statCountersArray[(int)NodeStatsEventType.Disconnect];
            }

            if (disconnectCount == 0)
            {
                disconnectDelay = 100;
            }
            else if (disconnectCount > _statsParameters.DisconnectDelays.Length)
            {
                disconnectDelay = _statsParameters.DisconnectDelays[^1];
            }
            else
            {
                disconnectDelay = _statsParameters.DisconnectDelays[disconnectCount - 1];
            }

            return disconnectDelay;
        }


        private long CalculateCurrentReputation()
        {
            return IsReputationPenalized() ? -100 : CurrentPersistedNodeReputation / 2 + CalculateSessionReputation();
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
                rlpxReputation +=  (rlpxReputation != 0 && !HasDisconnectedOnce) ? 1 : 0;
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
                if (_statsParameters.PenalizedReputationLocalDisconnectReasons.Contains(_lastLocalDisconnect.Value))
                {
                    return true;
                }
            }

            if (!_lastRemoteDisconnect.HasValue)
            {
                return false;
            }

            if (_statsParameters.PenalizedReputationRemoteDisconnectReasons.Contains(_lastRemoteDisconnect.Value))
            {
                if (_lastRemoteDisconnect == DisconnectReason.TooManyPeers || _lastRemoteDisconnect == DisconnectReason.AlreadyConnected)
                {
                    double timeFromLastDisconnect = DateTime.UtcNow.Subtract(_lastDisconnectTime ?? DateTime.MinValue).TotalMilliseconds;
                    return timeFromLastDisconnect < _statsParameters.PenalizedReputationTooManyPeersTimeout;
                }

                return true;
            }

            return false;
        }
    }
}
