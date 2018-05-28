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

using System.Net;
using Nethermind.Core;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Network;
using Nethermind.Network.P2P;

namespace Nethermind.Discovery
{
    public class DiscoveryConfigurationProvider : IDiscoveryConfigurationProvider
    {
        public DiscoveryConfigurationProvider(INetworkHelper networkHelper)
        {
            PongTimeout = 15000;
            BucketSize = 16;
            BucketsCount = 256;
            MasterExternalIp = // networkHelper.GetExternalIp()?.ToString();
            MasterHost = networkHelper.GetLocalIp()?.ToString() ?? "127.0.0.1";
            MasterPort = 30304;
            IsDiscoveryNodesPersistenceOn = true;
        }

        public int BucketSize { get; set; }
        public int BucketsCount { get; set; }
        public int Concurrency => 3;

        public int BitsPerHop => 8;

        //public string MasterHost => "127.0.0.1";
        public string MasterHost { get; set; } //=> "192.168.1.154";
        public string MasterExternalIp { get; set; }
        public int MasterPort { get; set; }
        public int MaxDiscoveryRounds => 8;
        public int EvictionCheckInterval => 75;
        public int SendNodeTimeout => 500;
        public int PongTimeout { get; set; }
        public int BootNodePongTimeout => 100000;
        public int PingRetryCount => 3;
        public int DiscoveryInterval => 30000;
        public int DiscoveryPersistanceInterval => 3000;
        public int DiscoveryNewCycleWaitTime => 50;
        public int RefreshInterval => 7200;

        public NetworkNode[] NetworkNodes { get; set; }

        public string KeyPass => "TestPass";
        public int UdpChannelCloseTimeout => 10000;
        public int PingMessageVersion => 4;
        public int DiscoveryMsgExpiryTime => 60 * 90;
        public int MaxNodeLifecycleManagersCount => 2000;
        public int NodeLifecycleManagersCleaupCount => 200;
        public long PredefiedReputation => 1000500;

        public DisconnectReason[] PenalizedReputationLocalDisconnectReasons => new[]
        {
            DisconnectReason.UnexpectedIdentity, DisconnectReason.IncompatibleP2PVersion, DisconnectReason.UselessPeer,
            DisconnectReason.BreachOfProtocol
        };

        public DisconnectReason[] PenalizedReputationRemoteDisconnectReasons => new[]
        {
            DisconnectReason.UnexpectedIdentity, DisconnectReason.IncompatibleP2PVersion, DisconnectReason.UselessPeer,
            DisconnectReason.BreachOfProtocol, DisconnectReason.TooManyPeers, DisconnectReason.AlreadyConnected
        };

        public long PenalizedReputationTooManyPeersTimeout => 10 * 1000;
        public Node[] TrustedNodes { get; set; }
        public string DbBasePath { get; set; }
        public bool IsDiscoveryNodesPersistenceOn { get; set; }
        public int ActivePeerUpdateInterval => 1000;
        public int ActivePeersMaxCount => 200;
    }
}