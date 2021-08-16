//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Config;

namespace Nethermind.Network.Config
{
    [ConfigCategory(DisabledForCli = false, HiddenFromDocs = true)]
    public interface IDiscoveryConfig : IConfig
    {
        /// <summary>
        /// Kademlia - k
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int BucketSize { get; set; }

        /// <summary>
        /// Buckets count
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int BucketsCount { get; set; }

        /// <summary>
        /// Kademlia - alpha
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int Concurrency { get; }

        /// <summary>
        /// Kademlia - b
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int BitsPerHop { get; }

        /// <summary>
        /// Max Discovery Rounds
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int MaxDiscoveryRounds { get; }

        /// <summary>
        /// Eviction check interval in ms
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int EvictionCheckInterval { get; }

        /// <summary>
        /// Send Node Timeout in ms
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int SendNodeTimeout { get; }

        /// <summary>
        /// Pong Timeout in ms
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int PongTimeout { get; set; }

        /// <summary>
        /// Boot Node Pong Timeout in ms
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int BootnodePongTimeout { get; }

        /// <summary>
        /// Pong Timeout in ms
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int PingRetryCount { get; }

        /// <summary>
        /// Time between running discovery processes in milliseconds
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int DiscoveryInterval { get; }

        /// <summary>
        /// Time between persisting discovered nodes in milliseconds
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int DiscoveryPersistenceInterval { get; }

        /// <summary>
        /// Time between discovery cycles in milliseconds
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int DiscoveryNewCycleWaitTime { get; }

        /// <summary>
        /// Boot nodes connection details
        /// </summary>
        string Bootnodes { get; set; }

        /// <summary>
        /// Timeout for closing UDP channel in milliseconds
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int UdpChannelCloseTimeout { get; }

        /// <summary>
        /// Maximum count of NodeLifecycleManagers stored in memory
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int MaxNodeLifecycleManagersCount { get; }

        /// <summary>
        /// Count of NodeLifecycleManagers to remove in one cleanup cycle
        /// </summary>
        [ConfigItem(DisabledForCli = true)]
        int NodeLifecycleManagersCleanupCount { get; }
    }
}
