// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Config;

namespace Nethermind.Network.Discovery;

public class DiscoveryConfig : IDiscoveryConfig
{
    public int BucketSize { get; set; } = 16;

    public int BucketsCount { get; set; } = 256;

    public int Concurrency { get; set; } = 3;

    public int BitsPerHop { get; set; } = 8;

    public int MaxDiscoveryRounds { get; set; } = 8;

    public int EvictionCheckInterval { get; set; } = 75;

    public int SendNodeTimeout { get; set; } = 500;

    public int PongTimeout { get; set; } = 1000 * 15;

    public int BootnodePongTimeout { get; set; } = 1000 * 100;

    public int PingRetryCount { get; set; } = 3;

    public int DiscoveryInterval { get; set; } = 1000 * 30;

    public int DiscoveryPersistenceInterval { get; set; } = 1000 * 180;

    public int DiscoveryNewCycleWaitTime { get; set; } = 50;

    public int UdpChannelCloseTimeout { get; set; } = 1000 * 5;

    public int MaxNodeLifecycleManagersCount { get; set; } = 8000;

    public int NodeLifecycleManagersCleanupCount { get; set; } = 4000;

    public string Bootnodes { get; set; } = string.Empty;
}
