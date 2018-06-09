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
using System.Linq;
using Nethermind.Core;
using Nethermind.Network.Discovery;
using Nethermind.Network.P2P;

namespace Nethermind.Network.Stats
{
    /// <summary>
    /// Initial version of Reputation calculation mostly based on EthereumJ impl
    /// </summary>
    public class NodeStats : INodeStats
    {
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private Dictionary<NodeStatsEvent, AtomicLong> _stats;
        private Dictionary<DisconnectType, (DisconnectReason DisconnectReason, DateTime DisconnectTime)> _lastDisconnects;

        public NodeStats(IDiscoveryConfigurationProvider configurationProvider)
        {
            _configurationProvider = configurationProvider;
            Initialize();
        }

        public void AddNodeStatsEvent(NodeStatsEvent nodeStatsEvent)
        {
            _stats[nodeStatsEvent].Increment();
        }

        public void AddNodeStatsDisconnectEvent(DisconnectType disconnectType, DisconnectReason disconnectReason)
        {
            _lastDisconnects[disconnectType] = (disconnectReason, DateTime.Now);
        }

        public bool DidEventHappen(NodeStatsEvent nodeStatsEvent)
        {
            return _stats[nodeStatsEvent].Value > 0;
        }

        public long CurrentNodeReputation => CalculateCurrentReputation();

        public long CurrentPersistedNodeReputation { get; set; }

        public long NewPersistedNodeReputation => IsReputationPenalized() ? 0 : (CurrentPersistedNodeReputation + CalculateSessionReputation()) / 2;

        public bool IsTrustedPeer { get; set; }

        public NodeDetails NodeDetails { get; private set; }

        private long CalculateCurrentReputation()
        {
            return IsReputationPenalized()
                ? 0
                : CurrentPersistedNodeReputation / 2 + CalculateSessionReputation() +
                  (IsTrustedPeer ? _configurationProvider.PredefiedReputation : 0);
        }

        private long CalculateSessionReputation()
        {
            long discoveryReputation = 0;
            discoveryReputation += Math.Min(_stats[NodeStatsEvent.DiscoveryPingIn].Value, 10) * (_stats[NodeStatsEvent.DiscoveryPingIn].Value == _stats[NodeStatsEvent.DiscoveryPingOut].Value ? 2 : 1);
            discoveryReputation += Math.Min(_stats[NodeStatsEvent.DiscoveryNeighboursIn].Value, 10) * 2;

            long rlpxReputation = 0;
            rlpxReputation += _stats[NodeStatsEvent.P2PInitialized].Value > 0 ? 10 : 0;
            rlpxReputation += _stats[NodeStatsEvent.Eth62Initialized].Value > 0 ? 20 : 0;

            if (_lastDisconnects.Any())
            {
                var localDisconnectReason = _lastDisconnects.ContainsKey(DisconnectType.Local) ? _lastDisconnects[DisconnectType.Local].DisconnectReason : (DisconnectReason?)null;
                var remoteDisconnectReason = _lastDisconnects.ContainsKey(DisconnectType.Remote) ? _lastDisconnects[DisconnectType.Remote].DisconnectReason : (DisconnectReason?)null;
                if (localDisconnectReason == DisconnectReason.Other || remoteDisconnectReason == DisconnectReason.Other)
                {
                    rlpxReputation = (long)(rlpxReputation * 0.3);
                }
                else if(localDisconnectReason != DisconnectReason.DisconnectRequested)
                {
                    if (remoteDisconnectReason == DisconnectReason.TooManyPeers)
                    {
                        rlpxReputation = (long) (rlpxReputation * 0.3);
                    }
                    else if (remoteDisconnectReason != DisconnectReason.DisconnectRequested)
                    {
                        rlpxReputation = (long) (rlpxReputation * 0.2);
                    }
                }
            }

            return discoveryReputation + 100 * rlpxReputation;
        }

        private bool IsReputationPenalized()
        {
            if (!_lastDisconnects.Any())
            {
                return false;
            }

            var lastOverallDisconnectTime = DateTime.MinValue;

            if (_lastDisconnects.ContainsKey(DisconnectType.Local))
            {
                var localDisconnect = _lastDisconnects[DisconnectType.Local];               
                if (_configurationProvider.PenalizedReputationLocalDisconnectReasons.Contains(localDisconnect.DisconnectReason))
                {
                    return true;
                }
                lastOverallDisconnectTime = localDisconnect.DisconnectTime;
            }

            if (!_lastDisconnects.ContainsKey(DisconnectType.Remote))
            {
                return false;
            }

            var remoteDisconnect = _lastDisconnects[DisconnectType.Remote];
            if (remoteDisconnect.DisconnectTime > lastOverallDisconnectTime)
            {
                lastOverallDisconnectTime = remoteDisconnect.DisconnectTime;
            }
            if (_configurationProvider.PenalizedReputationRemoteDisconnectReasons.Contains(remoteDisconnect.DisconnectReason))
            {
                if (new[] {DisconnectReason.AlreadyConnected, DisconnectReason.TooManyPeers}.Contains(remoteDisconnect.DisconnectReason))
                {
                    var timeFromLastDisconnect = DateTime.Now.Subtract(lastOverallDisconnectTime).TotalMilliseconds;
                    return timeFromLastDisconnect < _configurationProvider.PenalizedReputationTooManyPeersTimeout;
                }

                return true;
            }

            return false;
        }

        private void Initialize()
        {
            NodeDetails = new NodeDetails();
            IsTrustedPeer = false;
            _stats = new Dictionary<NodeStatsEvent, AtomicLong>();
            foreach (NodeStatsEvent statType in Enum.GetValues(typeof(NodeStatsEvent)))
            {
                _stats[statType] = new AtomicLong();
            }
            _lastDisconnects = new Dictionary<DisconnectType, (DisconnectReason DisconnectReason, DateTime DisconnectTime)>();
        }
    }
}
