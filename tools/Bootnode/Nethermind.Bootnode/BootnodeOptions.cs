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

internal static class BootnodeOptionDefaults
{
    private const string AnyAddress = "0.0.0.0";
    private const string LoopbackAddress = "127.0.0.1";

    public static bool IsRunningInContainer =>
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            bool.TrueString,
            StringComparison.OrdinalIgnoreCase);

    public static string DataDir(bool isRunningInContainer) =>
        Path.Combine(Environment.CurrentDirectory, isRunningInContainer ? "data" : "bootnode-data");

    public static string? LocalIp(bool isRunningInContainer) => isRunningInContainer ? AnyAddress : null;

    public static string ServiceHost(bool isRunningInContainer) => isRunningInContainer ? AnyAddress : LoopbackAddress;
}
