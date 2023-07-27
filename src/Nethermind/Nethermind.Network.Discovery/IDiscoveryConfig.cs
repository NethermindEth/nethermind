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
    [ConfigItem(DefaultValue = "16")]
    int BucketSize { get; set; }

    /// <summary>
    /// Buckets count.
    /// </summary>
    [ConfigItem(DisabledForCli = true)]
    int BucketsCount { get; set; }

    /// <summary>
    /// Kademlia - alpha
    /// </summary>
    [ConfigItem(DefaultValue = "3")]
    int Concurrency { get; }

    /// <summary>
    /// Kademlia - b
    /// </summary>
    [ConfigItem(DisabledForCli = true)]
    int BitsPerHop { get; }

    /// <summary>
    /// Max Discovery Rounds
    /// </summary>
    [ConfigItem(DefaultValue = "8")]
    int MaxDiscoveryRounds { get; }

    /// <summary>
    /// Eviction check interval in ms
    /// </summary>
    [ConfigItem(DefaultValue = "75")]
    int EvictionCheckInterval { get; }

    /// <summary>
    /// Send Node Timeout in ms
    /// </summary>
    [ConfigItem(DefaultValue = "500")]
    int SendNodeTimeout { get; }

    /// <summary>
    /// Pong Timeout in ms
    /// </summary>
    [ConfigItem(DefaultValue = "15000")]
    int PongTimeout { get; set; }

    /// <summary>
    /// Boot Node Pong Timeout in ms
    /// </summary>
    [ConfigItem(DefaultValue = "100000")]
    int BootnodePongTimeout { get; }

    /// <summary>
    /// Pong Timeout in ms
    /// </summary>
    [ConfigItem(DefaultValue = "3")]
    int PingRetryCount { get; }

    /// <summary>
    /// Time between running discovery processes in milliseconds
    /// </summary>
    [ConfigItem(DefaultValue = "30000")]
    int DiscoveryInterval { get; }

    /// <summary>
    /// Time between persisting discovered nodes in milliseconds
    /// </summary>
    [ConfigItem(DefaultValue = "180000")]
    int DiscoveryPersistenceInterval { get; }

    /// <summary>
    /// Time between discovery cycles in milliseconds
    /// </summary>
    [ConfigItem(DefaultValue = "50")]
    int DiscoveryNewCycleWaitTime { get; }

    /// <summary>
    /// Boot nodes connection details
    /// </summary>
    string Bootnodes { get; set; }

    /// <summary>
    /// Timeout for closing UDP channel in milliseconds
    /// </summary>
    [ConfigItem(DefaultValue = "5000")]
    int UdpChannelCloseTimeout { get; }

    /// <summary>
    /// Maximum count of NodeLifecycleManagers stored in memory
    /// </summary>
    [ConfigItem(DefaultValue = "8000")]
    int MaxNodeLifecycleManagersCount { get; }

    /// <summary>
    /// Count of NodeLifecycleManagers to remove in one cleanup cycle
    /// </summary>
    [ConfigItem(DefaultValue = "4000")]
    int NodeLifecycleManagersCleanupCount { get; }
}
