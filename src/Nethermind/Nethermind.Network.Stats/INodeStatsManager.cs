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

using System.Collections.Generic;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    public interface INodeStatsManager
    {
        INodeStats GetOrAdd(Node node);
        void ReportHandshakeEvent(Node node, ConnectionDirection direction);
        void ReportEvent(Node node, NodeStatsEventType eventType);
        (bool Result, NodeStatsEventType? DelayReason) IsConnectionDelayed(Node node);
        CompatibilityValidationType? FindCompatibilityValidationResult(Node node);
        long GetCurrentReputation(Node node);
        void ReportP2PInitializationEvent(Node node, P2PNodeDetails p2PNodeDetails);
        void ReportSyncPeerInitializeEvent(string Protocol, Node node, SyncPeerNodeDetails ethNodeDetails);
        void ReportFailedValidation(Node node, CompatibilityValidationType p2PVersion);
        void ReportDisconnect(Node node, DisconnectType disconnectType, DisconnectReason disconnectReason);
        long GetNewPersistedReputation(Node node);
        void ReportSyncEvent(Node node, NodeStatsEventType nodeStatsEvent);
        bool HasFailedValidation(Node node);
        void ReportTransferSpeedEvent(Node node, TransferSpeedType transferSpeedType, long value);
    }

    public enum TransferSpeedType
    {
        Latency,
        NodeData,
        Headers,
        Bodies,
        Receipts
    }
    
    public static class NodeStatsManagerExtension
    {
        public static void UpdateCurrentReputation(this INodeStatsManager nodeStatsManager, IEnumerable<Node> nodes)
        {
            foreach (Node node in nodes)
            {
                node.CurrentReputation = nodeStatsManager.GetCurrentReputation(node);
            }
        }

        public static void UpdateCurrentReputation(this INodeStatsManager nodeStatsManager, params Node[] nodes) =>
            UpdateCurrentReputation(nodeStatsManager, (IEnumerable<Node>)nodes);
    }
}
