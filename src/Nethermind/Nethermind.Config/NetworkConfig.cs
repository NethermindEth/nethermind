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

using System.IO;
using System.Linq;

namespace Nethermind.Config
{
    public class NetworkConfig : INetworkConfig
    {
        public int BucketSize { get; set; } = 16;
        public int BucketsCount { get; set; } = 256;
        public int Concurrency { get; set; } = 3;

        public int BitsPerHop { get; set; } = 8;

        //public string MasterHost => "127.0.0.1";
        public string MasterHost { get; set; } = "127.0.0.1"; //=> "192.168.1.154";
        public string MasterExternalIp { get; set; } = "127.0.0.1";
        public int MasterPort { get; set; } = 30304;
        public int MaxDiscoveryRounds { get; set; } = 8;
        public int EvictionCheckInterval { get; set; } = 75;
        public int SendNodeTimeout { get; set; } = 500;
        public int PongTimeout { get; set; } = 15000;
        public int BootNodePongTimeout { get; set; } = 100000;
        public int PingRetryCount { get; set; } = 3;
        public int DiscoveryInterval { get; set; } = 30000;
        public int DiscoveryPersistanceInterval { get; set; } = 1000 * 60 * 5;
        public int DiscoveryNewCycleWaitTime { get; set; } = 50;
        public int RefreshInterval { get; set; } = 7200;

        public ConfigNode[] BootNodes { get; set; } = new ConfigNode[0];

        public string KeyPass { get; set; } = "TestPass";
        public int UdpChannelCloseTimeout { get; set; } = 10000;
        public int PingMessageVersion { get; set; } = 4;
        public int DiscoveryMsgExpiryTime { get; set; } = 60 * 90;
        public int MaxNodeLifecycleManagersCount { get; set; } = 2000;
        public int NodeLifecycleManagersCleaupCount { get; set; } = 200;
        public long PredefinedReputation { get; set; } = 1000500;

        public ConfigDisconnectReason[] PenalizedReputationLocalDisconnectReasons { get; set; } = {
            ConfigDisconnectReason.UnexpectedIdentity, ConfigDisconnectReason.IncompatibleP2PVersion, ConfigDisconnectReason.UselessPeer,
            ConfigDisconnectReason.BreachOfProtocol
        };

        public ConfigDisconnectReason[] PenalizedReputationRemoteDisconnectReasons { get; set; } = {
            ConfigDisconnectReason.UnexpectedIdentity, ConfigDisconnectReason.IncompatibleP2PVersion, ConfigDisconnectReason.UselessPeer,
            ConfigDisconnectReason.BreachOfProtocol, ConfigDisconnectReason.TooManyPeers, ConfigDisconnectReason.AlreadyConnected
        };

        public long PenalizedReputationTooManyPeersTimeout { get; set; } = 10 * 1000;
        public ConfigNode[] TrustedPeers { get; set; } = new ConfigNode[0];
        public string DbBasePath { get; set; } = Path.GetTempPath();
        public bool IsDiscoveryNodesPersistenceOn { get; set; } = true;
        public bool IsPeersPersistenceOn { get; set; } = true;
        public int ActivePeerUpdateInterval { get; set; } = 10000;
        public bool IsActivePeerTimerEnabled { get; set; } = false;
        public int ActivePeersMaxCount { get; set; } = 25;
        public int DisconnectDelay { get; set; } = 1000 * 5;
        public int PeersPersistanceInterval { get; set; } = 1000 * 60 * 5;
        public int P2PPingInterval { get; set; } = 1000 * 10;
        public int P2PPingRetryCount { get; set; } = 3;
    }
}