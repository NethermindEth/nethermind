// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    public interface INodeStats
    {
        void AddNodeStatsEvent(NodeStatsEventType nodeStatsEventType);
        void AddNodeStatsHandshakeEvent(ConnectionDirection connectionDirection);
        void AddNodeStatsDisconnectEvent(DisconnectType disconnectType, DisconnectReason disconnectReason);
        void AddNodeStatsP2PInitializedEvent(P2PNodeDetails nodeDetails);
        void AddNodeStatsEth62InitializedEvent(SyncPeerNodeDetails nodeDetails);
        void AddNodeStatsLesInitializedEvent(SyncPeerNodeDetails nodeDetails);
        void AddNodeStatsSyncEvent(NodeStatsEventType nodeStatsEventType);

        bool DidEventHappen(NodeStatsEventType nodeStatsEventType);

        void AddTransferSpeedCaptureEvent(TransferSpeedType speedType, long bytesPerMillisecond);
        long? GetAverageTransferSpeed(TransferSpeedType speedType);
        (bool Result, NodeStatsEventType? DelayReason) IsConnectionDelayed(DateTime nowUTC);

        long CurrentNodeReputation() => CurrentNodeReputation(DateTime.UtcNow);
        long CurrentNodeReputation(DateTime nowUTC);
        long CurrentPersistedNodeReputation { get; set; }
        long NewPersistedNodeReputation(DateTime nowUTC);

        P2PNodeDetails P2PNodeDetails { get; }
        SyncPeerNodeDetails EthNodeDetails { get; }
        SyncPeerNodeDetails LesNodeDetails { get; }
        CompatibilityValidationType? FailedCompatibilityValidation { get; set; }

        /// <summary>
        /// Run a request sizer for the specific request type. The function passed in will receive a closure with the
        /// Original request size clamped down to a calculated limit. Depending on the latency and response size,
        /// the limit will be adjusted for subsequent calls.
        /// </summary>
        Task<TResponse> RunSizeAndLatencyRequestSizer<TResponse, TRequest, TResponseItem>(
            RequestType requestType,
            IReadOnlyList<TRequest> request,
            Func<IReadOnlyList<TRequest>, Task<(TResponse, long)>> func)
            where TResponse : IReadOnlyList<TResponseItem>;

        /// <summary>
        /// Run a request sizer for the specific request type. The function passed in will receive a closure with a limit
        /// Depending on the latency, the limit will be adjusted for subsequent calls.
        /// </summary>
        Task<TResponse> RunLatencyRequestSizer<TResponse>(RequestType requestType, Func<int, Task<TResponse>> func);

        /// <summary>
        /// Get the current request limit for the specific request type.
        /// </summary>
        int GetCurrentRequestLimit(RequestType requestType);
    }
}
