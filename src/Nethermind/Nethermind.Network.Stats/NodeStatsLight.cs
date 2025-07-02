// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FastEnumUtility;
using Nethermind.Core;
using Nethermind.Stats.Model;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Stats;

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
    private readonly Lock _speedLock = new();

    private DisconnectReason? _lastLocalDisconnect;
    private DisconnectReason? _lastRemoteDisconnect;

    private DateTime? _lastDisconnectTime;
    private DateTime? _lastFailedConnectionTime;

    private (DateTime, NodeStatsEventType) _delayConnectDeadline = (DateTime.UtcNow - TimeSpan.FromSeconds(1), NodeStatsEventType.None);

    private static readonly Random Random = new();

    private static readonly int _statsLength = FastEnum.GetValues<NodeStatsEventType>().Length;

    private readonly LatencyAndMessageSizeBasedRequestSizer _bodiesRequestSizer = new(
        minRequestLimit: 1,
        maxRequestLimit: 128,

        // In addition to the byte limit, we also try to keep the latency of the get block bodies between these two
        // watermark. This reduce timeout rate, and subsequently disconnection rate.
        lowerLatencyWatermark: TimeSpan.FromMilliseconds(2000),
        upperLatencyWatermark: TimeSpan.FromMilliseconds(3000),

        // When the bodies message size exceed this, we try to reduce the maximum number of block for this peer.
        // This is for BeSU and Reth which does not seems to use the 2MB soft limit, causing them to send 20MB of bodies
        // or receipts. This is not great as large message size are harder for DotNetty to pool byte buffer, causing
        // higher memory usage. Reducing this even further does seems to help with memory, but may reduce throughput.
        maxResponseSize: 3_000_000,
        initialRequestSize: 4
    );

    private readonly LatencyAndMessageSizeBasedRequestSizer _receiptsRequestSizer = new(
        minRequestLimit: 1,
        maxRequestLimit: 128,

        // In addition to the byte limit, we also try to keep the latency of the get receipts between these two
        // watermark. This reduce timeout rate, and subsequently disconnection rate.
        lowerLatencyWatermark: TimeSpan.FromMilliseconds(2000),
        upperLatencyWatermark: TimeSpan.FromMilliseconds(3000),

        // When the receipts message size exceed this, we try to reduce the maximum number of block for this peer.
        // This is for BeSU and Reth which does not seems to use the 2MB soft limit, causing them to send 20MB of bodies
        // or receipts. This is not great as large message size are harder for DotNetty to pool byte buffer, causing
        // higher memory usage. Reducing this even further does seems to help with memory, but may reduce throughput.
        maxResponseSize: 3_000_000,
        initialRequestSize: 8
    );

    private readonly LatencyBasedRequestSizer _snapRequestSizer = new(
        minRequestLimit: 50000,
        maxRequestLimit: 3_000_000,
        lowerWatermark: TimeSpan.FromMilliseconds(2000),
        upperWatermark: TimeSpan.FromMilliseconds(3500)
    );

    public NodeStatsLight(Node node, decimal latestSpeedWeight = 0.25m)
    {
        _statCountersArray = new int[_statsLength];
        _statsParameters = StatsParameters.Instance;
        _latestSpeedWeight = latestSpeedWeight;
        Node = node;
    }

    public long CurrentNodeReputation(DateTime nowUTC) => CalculateCurrentReputation(nowUTC);

    public long CurrentPersistedNodeReputation { get; set; }

    public long NewPersistedNodeReputation(DateTime nowUTC) => (CurrentPersistedNodeReputation + CalculateSessionReputation()) / 2;

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

        if (_statsParameters.EventParams.TryGetValue(nodeStatsEventType, out (TimeSpan delay, long _) param))
        {
            UpdateDelayConnectDeadline(DateTime.UtcNow, param.delay, nodeStatsEventType);
        }

        Increment(nodeStatsEventType);
    }

    public void AddNodeStatsHandshakeEvent(ConnectionDirection connectionDirection)
    {
        Increment(NodeStatsEventType.HandshakeCompleted);
    }

    public void AddNodeStatsDisconnectEvent(DisconnectType disconnectType, DisconnectReason disconnectReason)
    {
        DateTime nowUTC = DateTime.UtcNow;
        _lastDisconnectTime = nowUTC;
        if (disconnectType == DisconnectType.Local)
        {
            _lastLocalDisconnect = disconnectReason;
        }
        else
        {
            _lastRemoteDisconnect = disconnectReason;
        }

        if (disconnectType == DisconnectType.Local)
        {
            if (_statsParameters.LocalDisconnectParams.TryGetValue(disconnectReason, out (TimeSpan ReconnectDelay, long ReputationScore) param) && param.ReconnectDelay != TimeSpan.Zero)
            {
                UpdateDelayConnectDeadline(nowUTC, param.ReconnectDelay, NodeStatsEventType.LocalDisconnectDelay);
            }
        }
        else if (disconnectType == DisconnectType.Remote)
        {
            if (_statsParameters.RemoteDisconnectParams.TryGetValue(disconnectReason, out (TimeSpan ReconnectDelay, long ReputationScore) param) && param.ReconnectDelay != TimeSpan.Zero)
            {
                UpdateDelayConnectDeadline(nowUTC, param.ReconnectDelay, NodeStatsEventType.RemoteDisconnectDelay);
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
            TransferSpeedType.Latency => _averageLatency ?? int.MaxValue,
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
        return CurrentPersistedNodeReputation / 2 + CalculateSessionReputation();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetStat(NodeStatsEventType nodeStatsEventType)
    {
        return Volatile.Read(ref _statCountersArray[(int)nodeStatsEventType]);
    }

    private long CalculateSessionReputation()
    {
        long rlpxReputation = 0;

        rlpxReputation += Math.Min(GetStat(NodeStatsEventType.P2PPingIn), 10) * (GetStat(NodeStatsEventType.P2PPingIn) == GetStat(NodeStatsEventType.P2PPingOut) ? 2 : 1);

        if (_lastLocalDisconnect is not null)
        {
            if (_statsParameters.LocalDisconnectParams.TryGetValue(_lastLocalDisconnect.Value, out (TimeSpan ReconnectDelay, long ReputationScore) param))
            {
                rlpxReputation += param.ReputationScore;
            }
            else
            {
                rlpxReputation += _statsParameters.LocalDisconnectParams[DisconnectReason.Other].ReputationScore;
            }
        }

        if (_lastRemoteDisconnect is not null)
        {
            if (_statsParameters.RemoteDisconnectParams.TryGetValue(_lastRemoteDisconnect.Value, out (TimeSpan ReconnectDelay, long ReputationScore) param))
            {
                rlpxReputation += param.ReputationScore;
            }
            else
            {
                rlpxReputation += _statsParameters.RemoteDisconnectParams[DisconnectReason.Other].ReputationScore;
            }
        }

        foreach (KeyValuePair<NodeStatsEventType, (TimeSpan ReconnectDelay, long ReputationScore)> param in _statsParameters.EventParams)
        {
            if (DidEventHappen(param.Key))
            {
                rlpxReputation += param.Value.ReputationScore;
            }
        }

        return rlpxReputation;
    }

    public async Task<TResponse> RunSizeAndLatencyRequestSizer<TResponse, TRequest, TResponseItem>(RequestType requestType, IReadOnlyList<TRequest> request, Func<IReadOnlyList<TRequest>, Task<(TResponse, long)>> func) where TResponse : IReadOnlyList<TResponseItem>
    {
        if (requestType == RequestType.Bodies) return await _bodiesRequestSizer.Run<TResponse, TRequest, TResponseItem>(request, func);
        if (requestType == RequestType.Receipts) return await _receiptsRequestSizer.Run<TResponse, TRequest, TResponseItem>(request, func);

        throw new ArgumentException($"Unsupported request type: {requestType}");
    }

    public async Task<TResponse> RunLatencyRequestSizer<TResponse>(RequestType requestType, Func<int, Task<TResponse>> func)
    {
        if (requestType == RequestType.SnapRanges) return await _snapRequestSizer.MeasureLatency(func);

        throw new ArgumentException($"Unsupported request type: {requestType}");
    }

    public int GetCurrentRequestLimit(RequestType requestType)
    {
        if (requestType == RequestType.Bodies) return _bodiesRequestSizer.RequestSize;
        if (requestType == RequestType.Receipts) return _receiptsRequestSizer.RequestSize;
        if (requestType == RequestType.SnapRanges) return _snapRequestSizer.RequestSize;
        if (requestType == RequestType.Headers)
        {
            return Node.ClientType switch
            {
                NodeClientType.Nethermind => Stats.SyncLimits.NethermindSyncLimits.MaxHeaderFetch,
                NodeClientType.Geth => Stats.SyncLimits.GethSyncLimits.MaxHeaderFetch,
                NodeClientType.Parity => Stats.SyncLimits.ParitySyncLimits.MaxHeaderFetch,
                NodeClientType.Besu => Stats.SyncLimits.BeSuSyncLimits.MaxHeaderFetch,
                _ => Stats.SyncLimits.GethSyncLimits.MaxHeaderFetch, // Default to Geth limits for unknown clients
            };
        }

        throw new ArgumentException($"Unsupported request type: {requestType}");
    }
}
