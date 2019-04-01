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
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    public interface INodeStats
    {
        void AddNodeStatsEvent(NodeStatsEventType nodeStatsEventType);
        void AddNodeStatsHandshakeEvent(ConnectionDirection connectionDirection);
        void AddNodeStatsDisconnectEvent(DisconnectType disconnectType, DisconnectReason disconnectReason);
        void AddNodeStatsP2PInitializedEvent(P2PNodeDetails nodeDetails);
        void AddNodeStatsEth62InitializedEvent(EthNodeDetails nodeDetails);
        void AddNodeStatsSyncEvent(NodeStatsEventType nodeStatsEventType);

        bool DidEventHappen(NodeStatsEventType nodeStatsEventType);

        void AddLatencyCaptureEvent(NodeLatencyStatType latencyType, long milliseconds);
        long? GetAverageLatency(NodeLatencyStatType latencyType);

        (bool Result, NodeStatsEventType? DelayReason) IsConnectionDelayed();
        
        long CurrentNodeReputation { get; }
        long CurrentPersistedNodeReputation { get; set; }
        long NewPersistedNodeReputation { get; }
        
        P2PNodeDetails P2PNodeDetails { get; }
        EthNodeDetails EthNodeDetails { get; }
        CompatibilityValidationType? FailedCompatibilityValidation { get; set; }
        Node Node { get; }

        IEnumerable<NodeStatsEvent> EventHistory { get; }
        IEnumerable<NodeLatencyStatsEvent> LatencyHistory { get; }
    }
}