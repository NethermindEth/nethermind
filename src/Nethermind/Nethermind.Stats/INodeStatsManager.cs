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

using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    public interface INodeStatsManager
    {
        INodeStats GetOrAdd(Node node);
        void ReportHandshakeEvent(Node node, ConnectionDirection direction);
        void ReportEvent(Node node, NodeStatsEventType eventType);
        void DumpStats(bool logEventDetails);
        void DumpNodeStats(Node node);
        (bool Result, NodeStatsEventType? DelayReason) IsConnectionDelayed(Node node);
        CompatibilityValidationType? FindCompatibilityValidationResult(Node node);
        long GetCurrentReputation(Node node);
        void ReportP2PInitializationEvent(Node node, P2PNodeDetails p2PNodeDetails);
        void ReportEthInitializeEvent(Node node, EthNodeDetails ethNodeDetails);
        void ReportFailedValidation(Node node, CompatibilityValidationType p2PVersion);
        void ReportDisconnect(Node node, DisconnectType disconnectType, DisconnectReason disconnectReason);
        long GetNewPersistedReputation(Node node);
        long GetCurrentPersistedReputation(Node node);
        void ReportSyncEvent(Node node, NodeStatsEventType nodeStatsEvent);
        bool HasFailedValidation(Node node);
        void ReportLatencyCaptureEvent(Node node, NodeLatencyStatType latencyType, long value);
    }
}