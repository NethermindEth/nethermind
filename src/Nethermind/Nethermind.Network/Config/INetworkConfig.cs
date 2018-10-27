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

using Nethermind.Config;

namespace Nethermind.Network.Config
{
    public interface INetworkConfig : IConfig
    {
        /// <summary>
        /// Kademlia - k
        /// </summary>
        int BucketSize { get; set; }

        /// <summary>
        /// Buckets count
        /// </summary>
        int BucketsCount { get; set; }

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
        string MasterHost { get; set; }

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
        int PongTimeout { get; set; }

        /// <summary>
        /// Boot Node Pong Timeout in ms
        /// </summary>
        int BootNodePongTimeout { get; }

        /// <summary>
        /// Pong Timeout in ms
        /// </summary>
        int PingRetryCount { get; }

        /// <summary>
        /// Time between running discovery processes in milliseconds
        /// </summary>
        int DiscoveryInterval { get; }

        /// <summary>
        /// Time between persisting discovered nodes in milliseconds
        /// </summary>
        int DiscoveryPersistenceInterval { get; }

        /// <summary>
        /// Time between discovery cicles in milliseconds
        /// </summary>
        int DiscoveryNewCycleWaitTime { get; }

        ///// <summary>
        ///// Time between running refresh processes in milliseconds
        ///// </summary>
        //int RefreshInterval { get; }

        /// <summary>
        /// Boot nodes connection details
        /// </summary>
        ConfigNode[] BootNodes { get; set; }

        /// <summary>
        /// Key Pass
        /// </summary>
        string KeyPass { get; }

        /// <summary>
        /// Timeout for closing UDP channel in milliseconds
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
        /// List of trusted nodes - we connect to them and set predefined high reputation
        /// </summary>
        ConfigNode[] TrustedPeers { get; set; }

        /// <summary>
        /// Base path for discovery db
        /// </summary>
        string DbBasePath { get; set; } // TODO: move from Network config

        /// <summary>
        /// On/Off for discovery persistence
        /// </summary>
        bool IsDiscoveryNodesPersistenceOn { get; }

        /// <summary>
        /// On/Off for peers
        /// </summary>
        bool IsPeersPersistenceOn { get; }

        /// <summary>
        /// Time between running peer update in milliseconds
        /// </summary>
        int ActivePeerUpdateInterval { get; }

        /// <summary>
        /// On/Off for active peer timer - provides reconnections
        /// </summary>
        bool IsActivePeerTimerEnabled { get; }

        /// <summary>
        /// Max amount of active peers on the tcp level 
        /// </summary>
        int ActivePeersMaxCount { get; }

        /// <summary>
        /// Delay of connection retrying after disconnect
        /// </summary>
        int DisconnectDelay { get; }

        /// <summary>
        /// Delay of connection retrying after failed connection attempt
        /// </summary>
        int FailedConnectionDelay { get; }

        /// <summary>
        /// Time between persisting peers in milliseconds
        /// </summary>
        int PeersPersistenceInterval { get; }

        /// <summary>
        /// Time between sending p2p ping
        /// </summary>
        int P2PPingInterval { get; }

        /// <summary>
        /// Number of ping missed for disconnection
        /// </summary>
        int P2PPingRetryCount { get; }

        /// <summary>
        /// Whether we should capture Node Stats events history
        /// </summary>
        bool CaptureNodeStatsEventHistory { get; }

        /// <summary>
        /// Date format history
        /// </summary>
        string DetailedTimeDateFormat { get; }

    }
}