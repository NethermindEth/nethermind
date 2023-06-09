// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.Logging;
using Nethermind.Stats;

namespace Nethermind.Synchronization.Peers
{
    /// <summary>
    /// This class is responsible for logging / reporting lists of peers
    /// </summary>
    internal class SyncPeersReport
    {
        private StringBuilder _stringBuilder = new();
        private int _currentInitializedPeerCount;

        private readonly ISyncPeerPool _peerPool;
        private readonly INodeStatsManager _stats;
        private readonly ILogger _logger;

        public SyncPeersReport(ISyncPeerPool peerPool, INodeStatsManager statsManager, ILogManager logManager)
        {
            lock (_writeLock)
            {
                _peerPool = peerPool ?? throw new ArgumentNullException(nameof(peerPool));
                _stats = statsManager ?? throw new ArgumentNullException(nameof(statsManager));
                _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            }
        }

        private object _writeLock = new();

        private IEnumerable<PeerInfo> OrderedPeers => _peerPool.InitializedPeers
            .OrderByDescending(p => p.SyncPeer?.HeadNumber)
            .ThenByDescending(p => p.SyncPeer?.Node?.ClientId?.StartsWith("Nethermind") ?? false)
            .ThenByDescending(p => p.SyncPeer?.Node?.ClientId).ThenBy(p => p.SyncPeer?.Node?.Host);

        public void WriteFullReport()
        {
            if (!_logger.IsDebug)
            {
                return;
            }

            lock (_writeLock)
            {
                RememberState(out bool _);

                _logger.Debug(MakeSummaryReportForPeers(_peerPool.InitializedPeers, $"Sync peers - Connected: {_currentInitializedPeerCount} | All: {_peerPool.PeerCount} | Max: {_peerPool.PeerMaxCount}"));
                _logger.Debug(MakeReportForPeer(OrderedPeers, ""));
            }
        }

        public void WriteAllocatedReport()
        {
            lock (_writeLock)
            {
                if (!_logger.IsInfo)
                {
                    return;
                }

                RememberState(out bool changed);
                if (!changed)
                {
                    return;
                }

                var header = $"Allocated sync peers {_currentInitializedPeerCount}({_peerPool.PeerCount})/{_peerPool.PeerMaxCount}";
                if (_logger.IsDebug)
                {
                    _logger.Debug(MakeReportForPeer(OrderedPeers.Where(p => (p.AllocatedContexts & AllocationContexts.All) != AllocationContexts.None), header));
                }
                else
                {
                    _logger.Info(MakeSummaryReportForPeers(_peerPool.InitializedPeers, header));
                }
            }
        }

        internal string? MakeSummaryReportForPeers(IEnumerable<PeerInfo> peers, string header)
        {
            lock (_writeLock)
            {
                IEnumerable<IGrouping<string, PeerInfo>> peerGroups = peers.GroupBy(peerInfo => ParseClientInfo(peerInfo.SyncPeer.ClientId));
                float sum = peerGroups.Sum(x => x.Count());

                _stringBuilder.Append(header);
                _stringBuilder.Append(" |");

                bool isFirst = true;
                foreach (var peerGroup in peerGroups.OrderByDescending(x => x.Count()))
                {
                    if (isFirst)
                    {
                        isFirst = false;
                    }
                    else
                    {
                        _stringBuilder.Append(',');
                    }
                    _stringBuilder.Append($" {peerGroup.Key} ({peerGroup.Count() / sum,6:P2})");
                }

                string result = _stringBuilder.ToString();
                _stringBuilder.Clear();
                return result;
            }
        }

        private string ParseClientInfo(string clientInfo)
        {
            int index = clientInfo.IndexOf('/');
            if (index > 0)
            {
                return clientInfo[..index];
            }
            else
            {
                return "Unknown";
            }
        }

        internal string? MakeReportForPeer(IEnumerable<PeerInfo> peers, string header)
        {
            _stringBuilder.Append(header);
            bool headerAdded = false;
            foreach (PeerInfo peerInfo in peers)
            {
                if (!headerAdded)
                {
                    headerAdded = true;
                    AddPeerHeader();
                }
                _stringBuilder.AppendLine();
                AddPeerInfo(peerInfo);
            }

            string result = _stringBuilder.ToString();
            _stringBuilder.Clear();
            return result;
        }

        private void AddPeerInfo(PeerInfo peerInfo)
        {
            INodeStats stats = _stats.GetOrAdd(peerInfo.SyncPeer.Node);
            _stringBuilder.Append("   ").Append(peerInfo);
            _stringBuilder.Append('[').Append(GetPaddedAverageTransferSpeed(stats, TransferSpeedType.Latency));
            _stringBuilder.Append('|').Append(GetPaddedAverageTransferSpeed(stats, TransferSpeedType.Headers));
            _stringBuilder.Append('|').Append(GetPaddedAverageTransferSpeed(stats, TransferSpeedType.Bodies));
            _stringBuilder.Append('|').Append(GetPaddedAverageTransferSpeed(stats, TransferSpeedType.Receipts));
            _stringBuilder.Append('|').Append(GetPaddedAverageTransferSpeed(stats, TransferSpeedType.NodeData));
            _stringBuilder.Append('|').Append(GetPaddedAverageTransferSpeed(stats, TransferSpeedType.SnapRanges));
            _stringBuilder.Append(']');
            _stringBuilder.Append('[').Append(peerInfo.SyncPeer.ClientId).Append(']');
        }

        private string GetPaddedAverageTransferSpeed(INodeStats nodeStats, TransferSpeedType transferSpeedType)
        {
            long? speed = nodeStats.GetAverageTransferSpeed(transferSpeedType);
            if (speed is null)
            {
                return "     ";
            }
            return $"{speed,5}";
        }

        private void AddPeerHeader()
        {
            _stringBuilder.AppendLine();
            _stringBuilder.Append("===")
                                .Append("[Active][Sleep ][Peer(ProtocolVersion/Head/Host:Port/Direction)]")
                                .Append("[Transfer Speeds (L/H/B/R/N/S)      ]")
                                .Append("[Client Info (Name/Version/Operating System/Language)     ]")
                                .AppendLine();
            _stringBuilder.Append("----------------------------------------------------------------------" +
                "----------------------------------------------------------------------------------------");
        }

        private void RememberState(out bool initializedCountChanged)
        {
            int initializedPeerCount = _peerPool.InitializedPeersCount;
            initializedCountChanged = initializedPeerCount != _currentInitializedPeerCount;
            _currentInitializedPeerCount = initializedPeerCount;
        }
    }
}
