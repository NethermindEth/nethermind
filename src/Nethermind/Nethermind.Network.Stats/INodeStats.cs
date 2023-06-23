// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    public interface INodeStats
    {
        void AddNodeStatsEvent(NodeStatsEventType nodeStatsEventType);
        void AddNodeStatsHandshakeEvent(ConnectionDirection connectionDirection);
        void AddNodeStatsDisconnectEvent(DisconnectType disconnectType, EthDisconnectReason ethDisconnectReason);
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
    }
}
