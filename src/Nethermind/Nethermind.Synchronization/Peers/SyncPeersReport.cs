// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.Logging;
using Nethermind.Stats;
using Nethermind.Stats.Model;

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
                _logger.Debug(MakeReportForPeers(OrderedPeers, ""));
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

                if (_logger.IsDebug)
                {
                    var header = $"Allocated sync peers {_currentInitializedPeerCount}({_peerPool.PeerCount})/{_peerPool.PeerMaxCount}";
                    _logger.Debug(MakeReportForPeers(OrderedPeers.Where(p => (p.AllocatedContexts & AllocationContexts.All) != AllocationContexts.None), header));
                }
            }
        }

        internal string? MakeSummaryReportForPeers(IEnumerable<PeerInfo> peers, string header)
        {
            lock (_writeLock)
            {
                IEnumerable<IGrouping<NodeClientType, PeerInfo>> peerGroups = peers.GroupBy(peerInfo => peerInfo.SyncPeer.ClientType);
                float sum = peerGroups.Sum(x => x.Count());

                _stringBuilder.Append(header);
                _stringBuilder.Append(" |");
                bool isFirst = true;
                foreach (var peerGroup in peers.GroupBy(peerInfo => peerInfo.SyncPeer.Name).OrderBy(p => p.Key))
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
                _stringBuilder.Append(" |");

                PeersContextCounts activeContexts = new();
                PeersContextCounts sleepingContexts = new();
                foreach (PeerInfo peerInfo in peers)
                {
                    CountContexts(peerInfo.AllocatedContexts, ref activeContexts);
                    CountContexts(peerInfo.SleepingContexts, ref sleepingContexts);
                }

                _stringBuilder.Append(" Active: ");
                activeContexts.AppendTo(_stringBuilder, "None");
                _stringBuilder.Append(" | Sleeping: ");
                sleepingContexts.AppendTo(_stringBuilder, activeContexts.Total != activeContexts.None ? "None" : "All");
                _stringBuilder.Append(" |");

                isFirst = true;
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

            static void CountContexts(AllocationContexts contexts, ref PeersContextCounts contextCounts)
            {
                contextCounts.Total++;
                if (contexts == AllocationContexts.None)
                {
                    contextCounts.None++;
                    return;
                }

                contextCounts.Headers += contexts.HasFlag(AllocationContexts.Headers) ? 1 : 0;
                contextCounts.Bodies += contexts.HasFlag(AllocationContexts.Bodies) ? 1 : 0;
                contextCounts.Receipts += contexts.HasFlag(AllocationContexts.Receipts) ? 1 : 0;
                contextCounts.Blocks += contexts.HasFlag(AllocationContexts.Blocks) ? 1 : 0;
                contextCounts.State += contexts.HasFlag(AllocationContexts.State) ? 1 : 0;
                contextCounts.Witness += contexts.HasFlag(AllocationContexts.Witness) ? 1 : 0;
                contextCounts.Snap += contexts.HasFlag(AllocationContexts.Snap) ? 1 : 0;
            }
        }

        internal string? MakeReportForPeers(IEnumerable<PeerInfo> peers, string header)
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

        private struct PeersContextCounts
        {
            public int None { get; set; }
            public int Headers { get; set; }
            public int Bodies { get; set; }
            public int Receipts { get; set; }
            public int Blocks { get; set; }
            public int State { get; set; }
            public int Witness { get; set; }
            public int Snap { get; set; }
            public int Total { get; set; }

            public void AppendTo(StringBuilder sb, string allText)
            {
                if (Total == None)
                {
                    sb.Append(allText);
                    return;
                }

                bool added = false;

                if (Headers > 0) AddComma(sb, ref added).Append(Headers).Append(" Headers");
                if (Bodies > 0) AddComma(sb, ref added).Append(Bodies).Append(" Bodies");
                if (Receipts > 0) AddComma(sb, ref added).Append(Receipts).Append(" Receipts");
                if (Blocks > 0) AddComma(sb, ref added).Append(Blocks).Append(" Blocks");
                if (State > 0) AddComma(sb, ref added).Append(State).Append(" State");
                if (Witness > 0) AddComma(sb, ref added).Append(Witness).Append(" Witness");
                if (Snap > 0) AddComma(sb, ref added).Append(Snap).Append(" Snap");

                StringBuilder AddComma(StringBuilder sb, ref bool itemAdded)
                {
                    if (itemAdded)
                    {
                        sb.Append(", ");
                    }
                    else
                    {
                        itemAdded = true;
                    }
                    return sb;
                }
            }
        }
    }
}
