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
