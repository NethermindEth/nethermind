// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.Discovery;

namespace Nethermind.Bootnode;

internal sealed class BootnodeOptions
{
    public required string DataDir { get; init; }
    public required int DiscoveryPort { get; init; }
    public required string HttpHost { get; init; }
    public required int HttpPort { get; init; }
    public required string MetricsHost { get; init; }
    public required int MetricsPort { get; init; }
    public required DiscoveryVersion DiscoveryVersion { get; init; }
    public required bool ActiveDiscovery { get; init; }
    public required int ActiveDiscoveryJobs { get; init; }
    public required int BucketSize { get; init; }
    public required int Concurrency { get; init; }
    public required int DiscoveryIntervalMs { get; init; }
    public required string? LocalIp { get; init; }
    public required string? ExternalIp { get; init; }
    public required string? ExternalIpV4 { get; init; }
    public required string? ExternalIpV6 { get; init; }
    public required string[] Bootnodes { get; init; }
    public required bool UseDefaultDiscv5Bootnodes { get; init; }
    public required string LogLevel { get; init; }
    public required string? LogFile { get; init; }
    public required string? PrivateKey { get; init; }
    public required string? PrivateKeyFile { get; init; }
    public required bool GenKey { get; init; }
    public required bool WriteAddress { get; init; }
}
