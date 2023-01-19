// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        Receipts,
        SnapRanges
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
