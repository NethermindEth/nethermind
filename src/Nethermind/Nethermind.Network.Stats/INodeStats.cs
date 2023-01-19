// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        (bool Result, NodeStatsEventType? DelayReason) IsConnectionDelayed();

        long CurrentNodeReputation { get; }
        long CurrentPersistedNodeReputation { get; set; }
        long NewPersistedNodeReputation { get; }

        P2PNodeDetails P2PNodeDetails { get; }
        SyncPeerNodeDetails EthNodeDetails { get; }
        SyncPeerNodeDetails LesNodeDetails { get; }
        CompatibilityValidationType? FailedCompatibilityValidation { get; set; }
    }
}
