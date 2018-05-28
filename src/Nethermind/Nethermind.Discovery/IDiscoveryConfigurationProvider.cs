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

using Nethermind.Core;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Network.P2P;

namespace Nethermind.Discovery
{
    public interface IDiscoveryConfigurationProvider
    {
        /// <summary>
        /// Kademlia - k
        /// </summary>
        int BucketSize { get; }

        /// <summary>
        /// Buckets count
        /// </summary>
        int BucketsCount { get; }

        /// <summary>
        /// Kademlia - alpha
        /// </summary>
        int Concurrency { get; }

        /// <summary>
        /// Kademlia - b
        /// </summary>
        int BitsPerHop { get; }

        /// <summary>
        /// Current Node host
        /// </summary>
        string MasterHost { get; }

        /// <summary>
        /// Current Node external ip
        /// </summary>
        string MasterExternalIp { get; set; }

        /// <summary>
        /// Current Node port
        /// </summary>
        int MasterPort { get; set; }

        /// <summary>
        /// Max Discovery Rounds
        /// </summary>
        int MaxDiscoveryRounds { get; }

        /// <summary>
        /// Eviction check interval in ms
        /// </summary>
        int EvictionCheckInterval { get; }

        /// <summary>
        /// Send Node Timeout in ms
        /// </summary>
        int SendNodeTimeout { get; }

        /// <summary>
        /// Pong Timeout in ms
        /// </summary>
        int PongTimeout { get; }

        /// <summary>
        /// Boot Node Pong Timeout in ms
        /// </summary>
        int BootNodePongTimeout { get; }

        /// <summary>
        /// Pong Timeout in ms
        /// </summary>
        int PingRetryCount { get; }

        /// <summary>
        /// Time between running dicovery processes in miliseconds
        /// </summary>
        int DiscoveryInterval { get; }
        
        /// <summary>
        /// Time between persisting discovered nodes in miliseconds
        /// </summary>
        int DiscoveryPersistanceInterval { get; }

        /// <summary>
        /// Time between discovery cicles in miliseconds
        /// </summary>
        int DiscoveryNewCycleWaitTime { get; }

        /// <summary>
        /// Time between running refresh processes in miliseconds
        /// </summary>
        int RefreshInterval { get; }

        /// <summary>
        /// Boot nodes connection details
        /// </summary>
        NetworkNode[] NetworkNodes { get; }

        /// <summary>
        /// Key Pass
        /// </summary>
        string KeyPass { get; }

        /// <summary>
        /// Timeout for closing UDP channel in miliseconds
        /// </summary>
        int UdpChannelCloseTimeout { get; }

        /// <summary>
        /// Version of the Ping message
        /// </summary>
        int PingMessageVersion { get; }

        /// <summary>
        /// Ping expiry time in seconds
        /// </summary>
        int DiscoveryMsgExpiryTime { get; }

        /// <summary>
        /// Maximum count of NodeLifecycleManagers stored in memory
        /// </summary>
        int MaxNodeLifecycleManagersCount { get; }

        /// <summary>
        /// Count of NodeLifecycleManagers to remove in one cleanup cycle
        /// </summary>
        int NodeLifecycleManagersCleaupCount { get; }

        /// <summary>
        /// Value of predefied reputation for trusted nodes
        /// </summary>
        long PredefiedReputation { get; }

        /// <summary>
        /// Local disconnect reasons for penalizing node reputation
        /// </summary>
        DisconnectReason[] PenalizedReputationLocalDisconnectReasons { get; }

        /// <summary>
        /// Remote disconnect reasons for penalizing node reputation
        /// </summary>
        DisconnectReason[] PenalizedReputationRemoteDisconnectReasons { get; }

        /// <summary>
        /// Time within which we penalized peer if disconnection happends due to too many peers
        /// </summary>
        long PenalizedReputationTooManyPeersTimeout { get; }

        /// <summary>
        /// List of trusted nodes - we connect to them and set predefined high reputation
        /// </summary>
        Node[] TrustedNodes { get; set; }

        /// <summary>
        /// Base path for discovery db
        /// </summary>
        string DbBasePath { get; set; }

        /// <summary>
        /// On/Off for discovery persistance
        /// </summary>
        bool IsDiscoveryNodesPersistenceOn { get; set; }

        /// <summary>
        /// Time between running peer update in miliseconds
        /// </summary>
        int ActivePeerUpdateInterval { get; }

        /// <summary>
        /// Max amount of active peers on the tcp level 
        /// </summary>
        int ActivePeersMaxCount { get; }
    }
}