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
using System.IO;
using System.Linq;
using System.Text;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Stats
{
    public class StatsDumper : IStatsDumper
    {
        private const string DetailedTimeDateFormat = "yyyy-MM-dd HH:mm:ss.fff";

        private readonly ILogger _logger;
        private readonly IStatsConfig _statsConfig;
        private string _eventLogsDirectoryPath;

        public StatsDumper(ILogManager logManager, IStatsConfig statsConfig, string outputDir = null)
        {
            _logger = logManager.GetClassLogger();
            _statsConfig = statsConfig ?? throw new ArgumentNullException(nameof(statsConfig));

            var path = outputDir.GetApplicationResourcePath();
            _eventLogsDirectoryPath = Path.Combine(path, "networkLogs");
            if (!Directory.Exists(_eventLogsDirectoryPath))
            {
                Directory.CreateDirectory(_eventLogsDirectoryPath);
            }
        }

        public void DumpStats(IReadOnlyCollection<INodeStats> nodes, bool logEventDetails)
        {
            var eventTypes = Enum.GetValues(typeof(NodeStatsEventType)).OfType<NodeStatsEventType>().Where(x => !x.ToString().Contains("Discovery"))
                .OrderBy(x => x).ToArray();
            var eventStats = eventTypes.Select(x => new
            {
                EventType = x.ToString(),
                Count = nodes.Count(y => y.DidEventHappen(x))
            }).ToArray();

            var chains = nodes.Where(x => x.EthNodeDetails != null).GroupBy(x => x.EthNodeDetails.ChainId).Select(
                x => new {ChainName = ChainId.GetChainName((int) x.Key), Count = x.Count()}).ToArray();
            var clients = nodes.Where(x => x.P2PNodeDetails != null).Select(x => x.P2PNodeDetails.ClientId).GroupBy(x => x).Select(
                x => new {ClientId = x.Key, Count = x.Count()}).ToArray();
            var remoteDisconnect = nodes.Count(x => x.EventHistory.Any(y => y.DisconnectDetails != null && y.DisconnectDetails.DisconnectType == DisconnectType.Remote));

            var sb = new StringBuilder();
            sb.AppendLine($"Session stats | {DateTime.Now.ToString(DetailedTimeDateFormat)} | Peers: {nodes.Count}");
            sb.AppendLine($"Peers count with each EVENT:{Environment.NewLine}" +
                          $"{string.Join(Environment.NewLine, eventStats.Select(x => $"{x.EventType.ToString()}:{x.Count}"))}{Environment.NewLine}" +
                          $"Remote disconnect: {remoteDisconnect}{Environment.NewLine}{Environment.NewLine}" +
                          $"CHAINS: {Environment.NewLine}" +
                          $"{string.Join(Environment.NewLine, chains.Select(x => $"{x.ChainName}:{x.Count}"))}{Environment.NewLine}{Environment.NewLine}" +
                          $"CLIENTS:{Environment.NewLine}" +
                          $"{string.Join(Environment.NewLine, clients.Select(x => $"{x.ClientId}:{x.Count}"))}{Environment.NewLine}");

            var peersWithLatencyStats = nodes.Where(x => x.LatencyHistory.Any()).ToArray();
            if (peersWithLatencyStats.Any())
            {
                var latencyLog = GetLatencyComparisonLog(peersWithLatencyStats);
                sb.AppendLine(latencyLog);
            }

            if (_statsConfig.CaptureNodeStatsEventHistory && logEventDetails)
            {
                sb.AppendLine($"All peers ({nodes.Count}):");
                sb.AppendLine($"{string.Join(Environment.NewLine, nodes.Select(GetNodeLog))}{Environment.NewLine}");

                var peersWithEvents = nodes.Where(x => x.EventHistory.Any(y => y.EventType != NodeStatsEventType.NodeDiscovered)).ToArray();
                sb.AppendLine($"Logging {peersWithEvents.Length} peers log event histories");
                foreach (var peer in peersWithEvents)
                {
                    LogPeerEventHistory(peer);
                }
            }
            else
            {
                sb.AppendLine($"Detailed session log disabled, CaptureNodeStatsEventHistory: {_statsConfig.CaptureNodeStatsEventHistory}, logEventDetails: {logEventDetails}");
            }

            sb.AppendLine();
            var generalFilePath = Path.Combine(_eventLogsDirectoryPath, "generalStats.log");
            var content = sb.ToString();
            File.AppendAllText(generalFilePath, content);
            if (_logger.IsTrace) _logger.Trace(content);
        }

        public void DumpNodeStats(INodeStats nodeStats)
        {
            StringBuilder sb = BuildNodeStats(nodeStats);
            _logger.Trace(sb.ToString());
        }

        private StringBuilder BuildNodeStats(INodeStats nodeStats)
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"NodeEventHistory | {DateTime.Now.ToString(DetailedTimeDateFormat)}, {GetNodeLog(nodeStats)}");

            if (nodeStats.P2PNodeDetails != null)
            {
                sb.AppendLine($"P2P details: ClientId: {nodeStats.P2PNodeDetails.ClientId}, P2PVersion: {nodeStats.P2PNodeDetails.P2PVersion}, Capabilities: {GetCapabilities(nodeStats.P2PNodeDetails)}");
            }

            if (nodeStats.EthNodeDetails != null)
            {
                sb.AppendLine($"Eth62 details: ChainId: {ChainId.GetChainName((int) nodeStats.EthNodeDetails.ChainId)}, TotalDifficulty: {nodeStats.EthNodeDetails.TotalDifficulty}");
            }

            foreach (var statsEvent in nodeStats.EventHistory.OrderBy(x => x.EventDate).ToArray())
            {
                sb.Append($"{statsEvent.EventDate.ToString(DetailedTimeDateFormat)} | {statsEvent.EventType}");
                if (statsEvent.ConnectionDirection.HasValue)
                {
                    sb.Append($" | {statsEvent.ConnectionDirection.Value.ToString()}");
                }

                if (statsEvent.P2PNodeDetails != null)
                {
                    sb.Append($" | {statsEvent.P2PNodeDetails.ClientId} | v{statsEvent.P2PNodeDetails.P2PVersion} | {GetCapabilities(statsEvent.P2PNodeDetails)}");
                }

                if (statsEvent.EthNodeDetails != null)
                {
                    sb.Append($" | {ChainId.GetChainName((int) statsEvent.EthNodeDetails.ChainId)} | TotalDifficulty:{statsEvent.EthNodeDetails.TotalDifficulty}");
                }

                if (statsEvent.DisconnectDetails != null)
                {
                    sb.Append($" | {statsEvent.DisconnectDetails.DisconnectReason.ToString()} | {statsEvent.DisconnectDetails.DisconnectType.ToString()}");
                }

                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("Latency averages:");
            var averageLatencies = GetAverageLatencies(nodeStats);
            foreach (var latency in averageLatencies.Where(x => x.Value.HasValue))
            {
                sb.AppendLine($"{latency.Key.ToString()} = {latency.Value}");
            }

            if (nodeStats.LatencyHistory.Any())
            {
                sb.AppendLine("Latency events:");
                foreach (var statsEvent in nodeStats.LatencyHistory.OrderBy(x => x.StatType).ThenBy(x => x.CaptureTime).ToArray())
                {
                    sb.AppendLine($"{statsEvent.StatType.ToString()} | {statsEvent.CaptureTime.ToString(DetailedTimeDateFormat)} | {statsEvent.Latency}");
                }
            }

            return sb;
        }

        private string GetNodeLog(INodeStats nodeStats)
        {
            return $"Node: {nodeStats.Node.Id.ToString(false)}, Address: {nodeStats.Node.Host}:{nodeStats.Node.Port}, Trusted: {nodeStats.Node.IsTrusted}, Bootnode: {nodeStats.Node.IsBootnode}";
        }

        private void LogPeerEventHistory(INodeStats nodeStats)
        {
            var log = BuildNodeStats(nodeStats).ToString();
            var fileName = Path.Combine(_eventLogsDirectoryPath, $"{nodeStats.Node.Id.ToString(false)}.log");
            File.AppendAllText(fileName, log);
            if (_logger.IsTrace) _logger.Trace(log);
        }

        private string GetLatencyComparisonLog(INodeStats[] nodeStats)
        {
            var latencyDict = nodeStats
                .Select(x => new {Node = x.Node, Average = GetAverageLatencies(x)})
                .OrderBy(x => x.Average.Select(y => new {y.Key, y.Value}).FirstOrDefault(y => y.Key == NodeLatencyStatType.BlockHeaders)?.Value ?? 10000);
            return $"Overall latency stats: {Environment.NewLine}{string.Join(Environment.NewLine, latencyDict.Select(x => $"{x.Node.Id}: {string.Join(" | ", x.Average.Select(y => $"{y.Key.ToString()}: {y.Value?.ToString() ?? "-"}"))}"))}";
        }

        private string GetCapabilities(P2PNodeDetails nodeDetails)
        {
            if (nodeDetails.Capabilities == null || !nodeDetails.Capabilities.Any())
            {
                return "none";
            }

            return string.Join("|", nodeDetails.Capabilities.Select(x => $"{x.ProtocolCode}v{x.Version}"));
        }

        private static Dictionary<NodeLatencyStatType, long?> GetAverageLatencies(INodeStats nodeStats)
        {
            return Enum.GetValues(typeof(NodeLatencyStatType)).OfType<NodeLatencyStatType>().ToDictionary(x => x, nodeStats.GetAverageLatency);
        }
    }
}