// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using FastEnumUtility;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    /// <summary>
    /// Initial version of Reputation calculation mostly based on EthereumJ impl
    /// </summary>
    public class NodeStatsLight : INodeStats
    {
        private readonly StatsParameters _statsParameters;

        // How much weight to put on latest speed.
        // 1.0m means that the reported speed will always replaced with latest speed.
        // 0.5m means that the reported speed will be (oldSpeed + newSpeed)/2;
        // 0.25m here means that the latest weight affect the stored weight a bit for every report, resulting in a smoother
        // modification to account for jitter.
        private readonly decimal _latestSpeedWeight;

        private decimal? _averageNodesTransferSpeed;
        private decimal? _averageHeadersTransferSpeed;
        private decimal? _averageBodiesTransferSpeed;
        private decimal? _averageReceiptsTransferSpeed;
        private decimal? _averageSnapRangesTransferSpeed;
        private decimal? _averageLatency;

        private readonly int[] _statCountersArray;
        private readonly object _speedLock = new();

        private EthDisconnectReason? _lastLocalDisconnect;
        private EthDisconnectReason? _lastRemoteDisconnect;

        private DateTime? _lastDisconnectTime;
        private DateTime? _lastFailedConnectionTime;

        private (DateTime, NodeStatsEventType) _delayConnectDeadline = (DateTime.UtcNow - TimeSpan.FromSeconds(1), NodeStatsEventType.None);

        private static readonly Random Random = new();

        private static readonly int _statsLength = FastEnum.GetValues<NodeStatsEventType>().Count;

        public NodeStatsLight(Node node, decimal latestSpeedWeight = 0.25m)
        {
            _statCountersArray = new int[_statsLength];
            _statsParameters = StatsParameters.Instance;
            _latestSpeedWeight = latestSpeedWeight;
            Node = node;
        }

        public long CurrentNodeReputation(DateTime nowUTC) => CalculateCurrentReputation(nowUTC);

        public long CurrentPersistedNodeReputation { get; set; }

        public long NewPersistedNodeReputation(DateTime nowUTC) => IsReputationPenalized(nowUTC) ? -100 : (CurrentPersistedNodeReputation + CalculateSessionReputation()) / 2;

        public P2PNodeDetails P2PNodeDetails { get; private set; }

        public SyncPeerNodeDetails EthNodeDetails { get; private set; }

        public SyncPeerNodeDetails LesNodeDetails { get; private set; }

        public CompatibilityValidationType? FailedCompatibilityValidation { get; set; }

        public Node Node { get; }

        private void Increment(NodeStatsEventType nodeStatsEventType)
        {
            Interlocked.Increment(ref _statCountersArray[(int)nodeStatsEventType]);
        }

        public void AddNodeStatsEvent(NodeStatsEventType nodeStatsEventType)
        {
            if (nodeStatsEventType == NodeStatsEventType.ConnectionFailed)
            {
                _lastFailedConnectionTime = DateTime.UtcNow;
            }

            if (_statsParameters.DelayDueToEvent.TryGetValue(nodeStatsEventType, out TimeSpan delay))
            {
                UpdateDelayConnectDeadline(DateTime.UtcNow, delay, nodeStatsEventType);
            }

            Increment(nodeStatsEventType);
        }

        public void AddNodeStatsHandshakeEvent(ConnectionDirection connectionDirection)
        {
            Increment(NodeStatsEventType.HandshakeCompleted);
        }

        public void AddNodeStatsDisconnectEvent(DisconnectType disconnectType, EthDisconnectReason ethDisconnectReason)
        {
            DateTime nowUTC = DateTime.UtcNow;
            _lastDisconnectTime = nowUTC;
            if (disconnectType == DisconnectType.Local)
            {
                _lastLocalDisconnect = ethDisconnectReason;
            }
            else
            {
                _lastRemoteDisconnect = ethDisconnectReason;
            }

            if (disconnectType == DisconnectType.Local)
            {
                if (_statsParameters.DelayDueToLocalDisconnect.TryGetValue(ethDisconnectReason, out TimeSpan delay))
                {
                    UpdateDelayConnectDeadline(nowUTC, delay, NodeStatsEventType.LocalDisconnectDelay);
                }
            }
            else if (disconnectType == DisconnectType.Remote)
            {
                if (_statsParameters.DelayDueToRemoteDisconnect.TryGetValue(ethDisconnectReason, out TimeSpan delay))
                {
                    UpdateDelayConnectDeadline(nowUTC, delay, NodeStatsEventType.RemoteDisconnectDelay);
                }
            }

            Increment(NodeStatsEventType.Disconnect);
        }

        private void UpdateDelayConnectDeadline(DateTime nowUTC, TimeSpan delay, NodeStatsEventType reason)
        {
            DateTime newDeadline = nowUTC + delay;
            (DateTime currentDeadline, NodeStatsEventType _) = _delayConnectDeadline;
            if (newDeadline > currentDeadline)
            {
                _delayConnectDeadline = (newDeadline, reason);
            }
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
            return GetStat(nodeStatsEventType) > 0;
        }

        public void AddTransferSpeedCaptureEvent(TransferSpeedType transferSpeedType, long bytesPerMillisecond)
        {
            lock (_speedLock)
            {
                switch (transferSpeedType)
                {
                    case TransferSpeedType.Latency:
                        UpdateValue(ref _averageLatency, bytesPerMillisecond);
                        break;
                    case TransferSpeedType.NodeData:
                        UpdateValue(ref _averageNodesTransferSpeed, bytesPerMillisecond);
                        break;
                    case TransferSpeedType.Headers:
                        UpdateValue(ref _averageHeadersTransferSpeed, bytesPerMillisecond);
                        break;
                    case TransferSpeedType.Bodies:
                        UpdateValue(ref _averageBodiesTransferSpeed, bytesPerMillisecond);
                        break;
                    case TransferSpeedType.Receipts:
                        UpdateValue(ref _averageReceiptsTransferSpeed, bytesPerMillisecond);
                        break;
                    case TransferSpeedType.SnapRanges:
                        UpdateValue(ref _averageSnapRangesTransferSpeed, bytesPerMillisecond);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(transferSpeedType), transferSpeedType, null);
                }
            }
        }

        private void UpdateValue(ref decimal? currentValue, decimal newValue)
        {
            currentValue = ((currentValue ?? newValue) * (1.0m - _latestSpeedWeight)) + (newValue * _latestSpeedWeight);
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
                TransferSpeedType.SnapRanges => _averageSnapRangesTransferSpeed,
                _ => throw new ArgumentOutOfRangeException()
            });
        }

        public (bool Result, NodeStatsEventType? DelayReason) IsConnectionDelayed(DateTime nowUTC)
        {
            if (IsDelayedDueToDisconnect(nowUTC))
            {
                return (true, NodeStatsEventType.Disconnect);
            }

            if (IsDelayedDueToFailedConnection(nowUTC))
            {
                return (true, NodeStatsEventType.ConnectionFailed);
            }

            (DateTime outgoingDelayDeadline, NodeStatsEventType reason) = _delayConnectDeadline;
            if (outgoingDelayDeadline > nowUTC)
            {
                return (true, reason);
            }

            return (false, null);
        }

        private bool IsDelayedDueToDisconnect(DateTime nowUTC)
        {
            if (!_lastDisconnectTime.HasValue)
            {
                return false;
            }

            double timePassed = nowUTC.Subtract(_lastDisconnectTime.Value).TotalMilliseconds;
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

        private bool IsDelayedDueToFailedConnection(DateTime nowUTC)
        {
            if (!_lastFailedConnectionTime.HasValue)
            {
                return false;
            }

            double timePassed = nowUTC.Subtract(_lastFailedConnectionTime.Value).TotalMilliseconds;
            int failedConnectionDelay = GetFailedConnectionDelay();
            bool result = timePassed < failedConnectionDelay;

            return result;
        }

        private int GetFailedConnectionDelay()
        {
            int failedConnectionFailed = GetStat(NodeStatsEventType.ConnectionFailed);

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
            int disconnectCount = GetStat(NodeStatsEventType.Disconnect);

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

        private long CalculateCurrentReputation(DateTime nowUTC)
        {
            return IsReputationPenalized(nowUTC) ? -100 : CurrentPersistedNodeReputation / 2 + CalculateSessionReputation();
        }

        private bool HasDisconnectedOnce => _lastLocalDisconnect.HasValue || _lastRemoteDisconnect.HasValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetStat(NodeStatsEventType nodeStatsEventType)
        {
            return Volatile.Read(ref _statCountersArray[(int)nodeStatsEventType]);
        }

        private long CalculateSessionReputation()
        {
            long discoveryReputation = 0;
            long rlpxReputation = 0;

            discoveryReputation += Math.Min(GetStat(NodeStatsEventType.DiscoveryPingIn), 10) * (GetStat(NodeStatsEventType.DiscoveryPingIn) == GetStat(NodeStatsEventType.DiscoveryPingOut) ? 2 : 1);
            discoveryReputation += Math.Min(GetStat(NodeStatsEventType.DiscoveryNeighboursIn), 10) * 2;

            rlpxReputation += Math.Min(GetStat(NodeStatsEventType.P2PPingIn), 10) * (GetStat(NodeStatsEventType.P2PPingIn) == GetStat(NodeStatsEventType.P2PPingOut) ? 2 : 1);
            rlpxReputation += GetStat(NodeStatsEventType.HandshakeCompleted) > 0 ? 10 : 0;
            rlpxReputation += GetStat(NodeStatsEventType.P2PInitialized) > 0 ? 10 : 0;
            rlpxReputation += GetStat(NodeStatsEventType.Eth62Initialized) > 0 ? 20 : 0;
            rlpxReputation += GetStat(NodeStatsEventType.SyncStarted) > 0 ? 1000 : 0;
            rlpxReputation += (rlpxReputation != 0 && !HasDisconnectedOnce) ? 1 : 0;

            if (HasDisconnectedOnce)
            {
                if (_lastLocalDisconnect == EthDisconnectReason.Other || _lastRemoteDisconnect == EthDisconnectReason.Other)
                {
                    rlpxReputation = (long)(rlpxReputation * 0.3);
                }
                else if (_lastLocalDisconnect != EthDisconnectReason.DisconnectRequested)
                {
                    if (_lastRemoteDisconnect == EthDisconnectReason.TooManyPeers)
                    {
                        rlpxReputation = (long)(rlpxReputation * 0.3);
                    }
                    else if (_lastRemoteDisconnect != EthDisconnectReason.DisconnectRequested)
                    {
                        rlpxReputation = (long)(rlpxReputation * 0.2);
                    }
                }
            }

            if (DidEventHappen(NodeStatsEventType.ConnectionFailed))
            {
                rlpxReputation = (long)(rlpxReputation * 0.2);
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

        private bool IsReputationPenalized(DateTime nowUTC)
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
                if (_lastRemoteDisconnect == EthDisconnectReason.TooManyPeers || _lastRemoteDisconnect == EthDisconnectReason.AlreadyConnected)
                {
                    double timeFromLastDisconnect = nowUTC.Subtract(_lastDisconnectTime ?? DateTime.MinValue).TotalMilliseconds;
                    return timeFromLastDisconnect < _statsParameters.PenalizedReputationTooManyPeersTimeout;
                }

                return true;
            }

            return false;
        }
    }
}
