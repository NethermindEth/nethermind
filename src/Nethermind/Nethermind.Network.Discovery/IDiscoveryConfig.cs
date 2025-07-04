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
    [Obsolete]
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
    [Obsolete]
    int MaxDiscoveryRounds { get; }

    /// <summary>
    /// Eviction check interval in ms
    /// </summary>
    [ConfigItem(DefaultValue = "75")]
    [Obsolete]
    int EvictionCheckInterval { get; }

    /// <summary>
    /// Send Node Timeout in ms
    /// </summary>
    [ConfigItem(DefaultValue = "500")]
    int SendNodeTimeout { get; }

    /// <summary>
    /// Enr request timeout in ms
    /// </summary>
    [ConfigItem(DefaultValue = "1000")]
    long EnrTimeout { get; set; }

    /// <summary>
    /// Ping timeout in ms
    /// </summary>
    [ConfigItem(DefaultValue = "1000")]
    long PingTimeout { get; set; }

    /// <summary>
    /// Message expiry time in MS
    /// </summary>
    [ConfigItem(DefaultValue = "30000")]
    long MessageExpiryTime { get; set; }

    /// <summary>
    /// Time to wait after attempting to bond with a ping message
    /// </summary>
    [ConfigItem(DefaultValue = "500")]
    int BondWaitTime { get; set; }

    /// <summary>
    /// Boot Node Pong Timeout in ms
    /// </summary>
    [ConfigItem(DefaultValue = "100000")]
    [Obsolete]
    int BootnodePongTimeout { get; }

    /// <summary>
    /// Pong Timeout in ms
    /// </summary>
    [ConfigItem(DefaultValue = "3")]
    [Obsolete]
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
    [Obsolete]
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
    [Obsolete]
    int NodeLifecycleManagersCleanupCount { get; }

    [ConfigItem(DefaultValue = "0.05")]
    [Obsolete]
    float DropFullBucketNodeProbability { get; set; }

    [ConfigItem(Description = "Limit number of outgoing discovery message per second.", DefaultValue = "100", HiddenFromDocs = true)]
    int MaxOutgoingMessagePerSecond { get; set; }

    [ConfigItem(Description = "Discovery version(s) to enable", DefaultValue = "All", HiddenFromDocs = true)]
    DiscoveryVersion DiscoveryVersion { get; set; }

    [ConfigItem(Description = "Concurrent discovery job", DefaultValue = "10", HiddenFromDocs = true)]
    int ConcurrentDiscoveryJob { get; set; }
}
