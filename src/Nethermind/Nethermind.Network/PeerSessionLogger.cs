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
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Core.Utils;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public class PeerSessionLogger : IPeerSessionLogger
    {
        private readonly ILogger _logger;
        private readonly IStatsConfig _statsConfig;
        private readonly INetworkConfig _networkConfig;
        private readonly IPerfService _perfService;
        private string _eventLogsDirectoryPath;
        
        public PeerSessionLogger(ILogManager logManager, IConfigProvider configProvider, IPerfService perfService)
        {
            _perfService = perfService;
            _logger = logManager.GetClassLogger();
            _statsConfig = configProvider.GetConfig<IStatsConfig>();
            _networkConfig = configProvider.GetConfig<INetworkConfig>();
        }

        public void Init(string logsDirectory)
        {
            var path = logsDirectory ?? PathUtils.GetExecutingDirectory();
            _eventLogsDirectoryPath = Path.Combine(path, @"logs\logEvents");
            if (!Directory.Exists(_eventLogsDirectoryPath))
            {
                Directory.CreateDirectory(_eventLogsDirectoryPath);
            }
        }
        
        public void LogSessionStats(IReadOnlyCollection<Peer> activePeers, IReadOnlyCollection<Peer> candidatePeers, bool logEventDetails)
        {
            var key = _perfService.StartPerfCalc();
            var peers = activePeers.Concat(candidatePeers).GroupBy(x => x.Node.Id).Select(x => x.First()).ToArray();
            var eventTypes = Enum.GetValues(typeof(NodeStatsEventType)).OfType<NodeStatsEventType>().Where(x => !x.ToString().Contains("Discovery"))
                .OrderBy(x => x).ToArray();
            var eventStats = eventTypes.Select(x => new
            {
                EventType = x.ToString(),
                Count = peers.Count(y => y.NodeStats.DidEventHappen(x))
            }).ToArray();

            var chains = peers.Where(x => x.NodeStats.EthNodeDetails != null).GroupBy(x => x.NodeStats.EthNodeDetails.ChainId).Select(
                x => new {ChainName = ChainId.GetChainName((int) x.Key), Count = x.Count()}).ToArray();
            var clients = peers.Where(x => x.NodeStats.P2PNodeDetails != null).Select(x => x.NodeStats.P2PNodeDetails.ClientId).GroupBy(x => x).Select(
                x => new {ClientId = x.Key, Count = x.Count()}).ToArray();
            var remoteDisconnect = peers.Count(x => x.NodeStats.EventHistory.Any(y => y.DisconnectDetails != null && y.DisconnectDetails.DisconnectType == DisconnectType.Remote));

            var sb = new StringBuilder();
            sb.AppendLine($"Session stats | {DateTime.Now.ToString(_networkConfig.DetailedTimeDateFormat)} | Active peers: {activePeers.Count} | Candidate peers: {candidatePeers.Count}");
            sb.AppendLine($"Peers count with each EVENT:{Environment.NewLine}" +
                         $"{string.Join(Environment.NewLine, eventStats.Select(x => $"{x.EventType.ToString()}:{x.Count}"))}{Environment.NewLine}" +
                         $"Remote disconnect: {remoteDisconnect}{Environment.NewLine}{Environment.NewLine}" +
                         $"CHAINS: {Environment.NewLine}" +
                         $"{string.Join(Environment.NewLine, chains.Select(x => $"{x.ChainName}:{x.Count}"))}{Environment.NewLine}{Environment.NewLine}" +
                         $"CLIENTS:{Environment.NewLine}" +
                         $"{string.Join(Environment.NewLine, clients.Select(x => $"{x.ClientId}:{x.Count}"))}{Environment.NewLine}");

            var peersWithLatencyStats = peers.Where(x => x.NodeStats.LatencyHistory.Any()).ToArray();
            if (peersWithLatencyStats.Any())
            {
                var latencyLog = GetLatencyComparisonLog(peersWithLatencyStats);
                sb.AppendLine(latencyLog);
            }

            if (_statsConfig.CaptureNodeStatsEventHistory && logEventDetails)
            {
                sb.AppendLine($"All peers ({peers.Length}):");
                sb.AppendLine($"{string.Join(Environment.NewLine, peers.Select(x => GetNodeLog(x.NodeStats)))}{Environment.NewLine}");

                var peersWithEvents = peers.Where(x => x.NodeStats.EventHistory.Any(y => y.EventType != NodeStatsEventType.NodeDiscovered)).ToArray();
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

            var logTime = _perfService.EndPerfCalc(key);
            if (_logger.IsDebug) _logger.Debug($"LogSessionStats time: {logTime} ms");
        }
        
        public string GetEventHistoryLog(INodeStats nodeStats)
        {   
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"NodeEventHistory | {DateTime.Now.ToString(_networkConfig.DetailedTimeDateFormat)}, {GetNodeLog(nodeStats)}");

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
                sb.Append($"{statsEvent.EventDate.ToString(_networkConfig.DetailedTimeDateFormat)} | {statsEvent.EventType}");
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

                if (statsEvent.SyncNodeDetails != null && (statsEvent.SyncNodeDetails.NodeBestBlockNumber.HasValue || statsEvent.SyncNodeDetails.OurBestBlockNumber.HasValue))
                {
                    sb.Append($" | NodeBestBlockNumber: {statsEvent.SyncNodeDetails.NodeBestBlockNumber} | OurBestBlockNumber: {statsEvent.SyncNodeDetails.OurBestBlockNumber}");
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
                    sb.AppendLine($"{statsEvent.StatType.ToString()} | {statsEvent.CaptureTime.ToString(_networkConfig.DetailedTimeDateFormat)} | {statsEvent.Latency}");
                }
            }

            return sb.ToString();
        }
        
        private string GetNodeLog(INodeStats nodeStats)
        {
            var isBootnode = NetworkNode.ParseNodes(_networkConfig.Bootnodes).Any(x => x.NodeId == nodeStats.Node.Id);
            return $"Node: {nodeStats.Node.Id.ToString(false)}, Address: {nodeStats.Node.Host}:{nodeStats.Node.Port}, Desc: {nodeStats.Node.Description}, Trusted: {nodeStats.IsTrustedPeer}, Bootnode: {isBootnode}";
        }
        
        private void LogPeerEventHistory(Peer peer)
        {
            var log = GetEventHistoryLog(peer.NodeStats);
            var fileName = Path.Combine(_eventLogsDirectoryPath, $"{peer.Node.Id.ToString(false)}.log");
            File.AppendAllText(fileName, log);
            if (_logger.IsTrace) _logger.Trace(log);
        }

        private string GetLatencyComparisonLog(Peer[] peers)
        {
            var latencyDict = peers.Select(x => new {x, Av = GetAverageLatencies(x.NodeStats)}).OrderBy(x => x.Av.Select(y => new {y.Key, y.Value}).FirstOrDefault(y => y.Key == NodeLatencyStatType.BlockHeaders)?.Value ?? 10000);
            return $"Overall latency stats: {Environment.NewLine}{string.Join(Environment.NewLine, latencyDict.Select(x => $"{x.x.Node.Id}: {string.Join(" | ", x.Av.Select(y => $"{y.Key.ToString()}: {y.Value?.ToString() ?? "-"}"))}"))}";
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