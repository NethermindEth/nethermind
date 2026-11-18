// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;

namespace Nethermind.Network.Config
{
    public class NetworkConfig : INetworkConfig
    {
        public string? ExternalIp { get; set; }

        /// <inheritdoc/>
        public string? ExternalIpV4 { get; set; }

        /// <inheritdoc/>
        public string? ExternalIpV6 { get; set; }

        /// <inheritdoc/>
        public bool EnableExternalIpResolution { get; set; } = true;

        public string? LocalIp { get; set; }
        public string? StaticPeers { get; set; }
        public string? DiscoveryDns { get; set; }

        public bool OnlyStaticPeers { get; set; }
        public bool IsPeersPersistenceOn { get; set; } = true;

        [Obsolete]
        public int ActivePeersMaxCount { get => MaxActivePeers; set => MaxActivePeers = value; }
        public int MaxActivePeers { get; set; } = 50;
        private int _maxPendingInboundConnections = 50;
        public int MaxPendingInboundConnections
        {
            get => _maxPendingInboundConnections;
            set => _maxPendingInboundConnections = ValidateMaxPendingInboundConnections(value);
        }

        internal static int ValidateMaxPendingInboundConnections(int value) => value >= 0 ? value
            : ThrowNegativeMaxPendingInboundConnections();

        private int _maxPendingInboundConnectionsPerSource = 4;
        /// <inheritdoc/>
        public int MaxPendingInboundConnectionsPerSource
        {
            get => _maxPendingInboundConnectionsPerSource;
            set => _maxPendingInboundConnectionsPerSource = ValidateMaxPendingInboundConnectionsPerSource(value);
        }

        internal static int ValidateMaxPendingInboundConnectionsPerSource(int value) => value >= 0 ? value
            : ThrowNegativeMaxPendingInboundConnectionsPerSource();

        public int PriorityPeersMaxCount { get; set; } = 0;
        public int PeersPersistenceInterval { get; set; } = 1000 * 5;
        public int PeersUpdateInterval { get; set; } = 250;
        public int P2PPingInterval { get; set; } = 1000 * 10;
        public int MaxPersistedPeerCount { get; set; } = 2000;
        public int PersistedPeerCountCleanupThreshold { get; set; } = 2200;
        public int MaxCandidatePeerCount { get; set; } = 10000;
        public int CandidatePeerCountCleanupThreshold { get; set; } = 11000;
        public bool DiagTracerEnabled { get; set; } = false;
        public int NettyArenaOrder { get; set; } = INetworkConfig.DefaultNettyArenaOrder;
        public uint MaxNettyArenaCount { get; set; } = INetworkConfig.DefaultMaxNettyArenaCount;
        public NetworkNode[] Bootnodes { get; set; } = [];
        public bool EnableUPnP { get; set; } = false;
        public int DiscoveryPort { get; set; } = 30303;
        public int P2PPort { get; set; } = 30303;
        public long SimulateSendLatencyMs { get; set; } = 0;
        public int NumConcurrentOutgoingConnects { get; set; } = 0;
        public int MaxOutgoingConnectPerSec { get; set; } = 20;
        public int ConnectTimeoutMs { get; set; } = 2000;
        public int ProcessingThreadCount { get; set; } = 1;
        public string? ClientIdMatcher { get; set; } = null;
        public bool DisableDiscV4DnsFeeder { get; set; } = false;
        public long RlpxHostShutdownCloseTimeoutMs { get; set; } = 1000;
        public string PublicClientIdFormat { get; set; } = ProductInfo.DefaultPublicClientIdFormat;
        public bool EnableEnrDiscovery { get; set; } = true;
        public bool FilterPeersByRecentIp { get; set; } = true;
        public bool FilterPeersBySameSubnet { get; set; } = true;
        public bool FilterDiscoveryNodesByRecentIp { get; set; } = true;
        public bool FilterDiscoveryNodesBySameSubnet { get; set; } = true;

        [DoesNotReturn, StackTraceHidden]
        private static int ThrowNegativeMaxPendingInboundConnections() =>
            throw new InvalidConfigurationException("Network.MaxPendingInboundConnections must not be negative.", ExitCodes.ForbiddenOptionValue);

        [DoesNotReturn, StackTraceHidden]
        private static int ThrowNegativeMaxPendingInboundConnectionsPerSource() =>
            throw new InvalidConfigurationException("Network.MaxPendingInboundConnectionsPerSource must not be negative.", ExitCodes.ForbiddenOptionValue);
    }
}
