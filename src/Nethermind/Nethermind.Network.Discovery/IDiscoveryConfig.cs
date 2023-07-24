// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Network.Discovery;

[ConfigCategory(DisabledForCli = false, HiddenFromDocs = true)]
public interface IDiscoveryConfig : IConfig
{
    /// <summary>
    /// Kademlia - k
    /// </summary>
    [ConfigItem]
    int BucketSize { get; set; }

    /// <summary>
    /// Buckets count
    /// </summary>
    [ConfigItem]
    int BucketsCount { get; set; }

    /// <summary>
    /// Kademlia - alpha
    /// </summary>
    [ConfigItem]
    int Concurrency { get; }

    /// <summary>
    /// Kademlia - b
    /// </summary>
    [ConfigItem]
    int BitsPerHop { get; }

    /// <summary>
    /// Max Discovery Rounds
    /// </summary>
    [ConfigItem]
    int MaxDiscoveryRounds { get; }

    /// <summary>
    /// Eviction check interval in ms
    /// </summary>
    [ConfigItem]
    int EvictionCheckInterval { get; }

    /// <summary>
    /// Send Node Timeout in ms
    /// </summary>
    [ConfigItem]
    int SendNodeTimeout { get; }

    /// <summary>
    /// Pong Timeout in ms
    /// </summary>
    [ConfigItem]
    int PongTimeout { get; set; }

    /// <summary>
    /// Boot Node Pong Timeout in ms
    /// </summary>
    [ConfigItem]
    int BootnodePongTimeout { get; }

    /// <summary>
    /// Pong Timeout in ms
    /// </summary>
    [ConfigItem]
    int PingRetryCount { get; }

    /// <summary>
    /// Time between running discovery processes in milliseconds
    /// </summary>
    [ConfigItem]
    int DiscoveryInterval { get; }

    /// <summary>
    /// Time between persisting discovered nodes in milliseconds
    /// </summary>
    [ConfigItem]
    int DiscoveryPersistenceInterval { get; }

    /// <summary>
    /// Time between discovery cycles in milliseconds
    /// </summary>
    [ConfigItem]
    int DiscoveryNewCycleWaitTime { get; }

    /// <summary>
    /// Boot nodes connection details
    /// </summary>
    string Bootnodes { get; set; }

    /// <summary>
    /// Timeout for closing UDP channel in milliseconds
    /// </summary>
    [ConfigItem]
    int UdpChannelCloseTimeout { get; }

    /// <summary>
    /// Maximum count of NodeLifecycleManagers stored in memory
    /// </summary>
    [ConfigItem]
    int MaxNodeLifecycleManagersCount { get; }

    /// <summary>
    /// Count of NodeLifecycleManagers to remove in one cleanup cycle
    /// </summary>
    [ConfigItem]
    int NodeLifecycleManagersCleanupCount { get; }
}
