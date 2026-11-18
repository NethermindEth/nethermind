// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    public interface INodeStatsManager
    {
        INodeStats GetOrAdd(Node node);
        /// <summary>Returns retained statistics, or null when the cache cannot admit the node.</summary>
        /// <remarks>Decorators should forward this member. Known capacity refusals return null.</remarks>
        INodeStats? GetStatsOrNull(Node node)
        {
            try { return GetOrAdd(node); }
            catch (NodeStatsCapacityException) { return null; }
        }
        /// <summary>Attempts to retain statistics until the returned lease is disposed.</summary>
        /// <remarks>Lookup alone does not guarantee ownership. Bounded managers and decorators must implement or forward acquisition.</remarks>
        bool TryAcquire(Node node, bool priority, [NotNullWhen(true)] out INodeStats? stats, out IDisposable? lease)
        {
            stats = null;
            lease = null;
            return false;
        }
        /// <summary>Promotes an owned lease to priority admission.</summary>
        /// <remarks>Decorators must forward promotion to the manager that issued the lease.</remarks>
        void PromoteLease(IDisposable? lease) { }
        /// <summary>Checks retained inbound-ban evidence without admitting statistics.</summary>
        /// <remarks>Decorators must forward this query, including when statistics capacity is exhausted.</remarks>
        bool IsInboundBanned(Node node, DateTime nowUtc) => GetStatsOrNull(node)?.IsInboundBanned(nowUtc) ?? true;
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
        SnapRanges,
        BlockAccessLists
    }

    public enum RequestType
    {
        Headers,
        Bodies,
        Receipts,
        SnapRanges,
        BlockAccessLists
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
