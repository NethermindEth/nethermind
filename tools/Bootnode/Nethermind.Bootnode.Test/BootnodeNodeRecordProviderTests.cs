// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class BootnodeNodeRecordProviderTests
{
    [Test]
    public async Task Discovery_only_node_record_omits_tcp_endpoint()
    {
        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, TestContext.CurrentContext.WorkDirectory);
        NetworkConfig networkConfig = new()
        {
            DiscoveryPort = 30303,
            P2PPort = 0
        };
        BootnodeNodeRecordProvider provider = new(
            protectedPrivateKey,
            new StaticIpResolver(IPAddress.Loopback),
            new EthereumEcdsa(1),
            networkConfig,
            LimboLogs.Instance,
            new BootnodeExternalIps(IPAddress.Loopback, IPAddress.Loopback, null),
            TestContext.CurrentContext.WorkDirectory);

        NodeRecord nodeRecord = await provider.GetCurrentAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRecord.DiscoveryPort, Is.EqualTo(30303));
            Assert.That(nodeRecord.TcpPort, Is.Null);
        }
    }

    [TestCase("192.0.2.1", "192.0.2.1", null)]
    [TestCase("::ffff:192.0.2.1", "192.0.2.1", null)]
    [TestCase("fd00:beef:cafe::11", null, "fd00:beef:cafe::11")]
    [TestCase("255.255.255.255", null, null)]
    public async Task Discovery_only_node_record_publishes_endpoint_entries_matching_external_ip_family(
        string externalIp,
        string? expectedIp,
        string? expectedIp6)
    {
        string dataDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, dataDir);
        NetworkConfig networkConfig = new()
        {
            DiscoveryPort = 30303,
            P2PPort = 0
        };

        NodeRecord nodeRecord = await CreateProvider(protectedPrivateKey, dataDir, networkConfig, IPAddress.Parse(externalIp)).GetCurrentAsync();
        NodeRecord decoded = NodeRecord.FromEnrString(nodeRecord.ToString());

        AssertEndpointEntries(decoded, expectedIp, expectedIp6);
    }

    [Test]
    public async Task Discovery_only_node_record_publishes_both_endpoint_families_when_configured()
    {
        string dataDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, dataDir);
        NetworkConfig networkConfig = new()
        {
            DiscoveryPort = 30303,
            P2PPort = 0
        };

        BootnodeExternalIps externalIps = new(
            IPAddress.Parse("192.0.2.1"),
            IPAddress.Parse("192.0.2.1"),
            IPAddress.Parse("2001:db8::1"));

        NodeRecord nodeRecord = await CreateProvider(protectedPrivateKey, dataDir, networkConfig, IPAddress.None, externalIps).GetCurrentAsync();
        NodeRecord decoded = NodeRecord.FromEnrString(nodeRecord.ToString());

        AssertEndpointEntries(decoded, "192.0.2.1", "2001:db8::1");
    }

    private static void AssertEndpointEntries(NodeRecord decoded, string? expectedIp, string? expectedIp6)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.GetObj<IPAddress>(EnrContentKey.Ip), Is.EqualTo(expectedIp is null ? null : IPAddress.Parse(expectedIp)));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Tcp), Is.Null);
            Assert.That(decoded.GetValue<int>(EnrContentKey.Udp), Is.EqualTo(expectedIp is null ? null : (int?)30303));
            Assert.That(decoded.GetObj<IPAddress>(EnrContentKey.Ip6), Is.EqualTo(expectedIp6 is null ? null : IPAddress.Parse(expectedIp6)));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Tcp6), Is.Null);
            Assert.That(decoded.GetValue<int>(EnrContentKey.Udp6), Is.EqualTo(expectedIp6 is null ? null : (int?)30303));
        }
    }

    [Test]
    public async Task Corrupt_enr_sequence_state_does_not_block_identity_creation()
    {
        string dataDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        await File.WriteAllTextAsync(Path.Combine(dataDir, "enr-state.json"), "{");

        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, dataDir);
        NetworkConfig networkConfig = new()
        {
            DiscoveryPort = 30303,
            P2PPort = 0
        };

        NodeRecord nodeRecord = await CreateProvider(protectedPrivateKey, dataDir, networkConfig, IPAddress.Loopback).GetCurrentAsync();
        string stateText = await File.ReadAllTextAsync(Path.Combine(dataDir, "enr-state.json"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeRecord.EnrSequence, Is.EqualTo(1));
            Assert.That(stateText, Does.Contain(nameof(BootnodeIdentity.EnrSequence)));
        }
    }

    [Test]
    public async Task Enr_sequence_is_reused_until_record_content_changes()
    {
        string dataDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, dataDir);
        NetworkConfig networkConfig = new()
        {
            DiscoveryPort = 30303,
            P2PPort = 0
        };

        NodeRecord firstRecord = await CreateProvider(protectedPrivateKey, dataDir, networkConfig, IPAddress.Loopback).GetCurrentAsync();
        NodeRecord sameRecord = await CreateProvider(protectedPrivateKey, dataDir, networkConfig, IPAddress.Loopback).GetCurrentAsync();
        NodeRecord changedRecord = await CreateProvider(protectedPrivateKey, dataDir, networkConfig, IPAddress.Parse("127.0.0.2")).GetCurrentAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstRecord.EnrSequence, Is.EqualTo(1));
            Assert.That(sameRecord.EnrSequence, Is.EqualTo(firstRecord.EnrSequence));
            Assert.That(changedRecord.EnrSequence, Is.EqualTo(firstRecord.EnrSequence + 1));
        }
    }

    private static BootnodeNodeRecordProvider CreateProvider(
        IProtectedPrivateKey protectedPrivateKey,
        string dataDir,
        INetworkConfig networkConfig,
        IPAddress ipAddress,
        BootnodeExternalIps? externalIps = null) =>
        new(
            protectedPrivateKey,
            new StaticIpResolver(ipAddress),
            new EthereumEcdsa(1),
            networkConfig,
            LimboLogs.Instance,
            externalIps ?? BootnodeExternalIps.Create(new BootnodeOptions
            {
                DataDir = dataDir,
                DiscoveryPort = networkConfig.DiscoveryPort,
                HttpHost = "127.0.0.1",
                HttpPort = 8546,
                MetricsHost = "127.0.0.1",
                MetricsPort = 6060,
                DiscoveryVersion = Nethermind.Network.Discovery.DiscoveryVersion.All,
                ActiveDiscovery = false,
                ActiveDiscoveryJobs = 0,
                BucketSize = 16,
                Concurrency = 3,
                DiscoveryIntervalMs = 30000,
                LocalIp = null,
                ExternalIp = null,
                ExternalIpV4 = null,
                ExternalIpV6 = null,
                Bootnodes = [],
                UseDefaultDiscv5Bootnodes = false,
                LogLevel = "Error",
                LogFile = null,
                PrivateKey = null,
                PrivateKeyFile = null,
                GenKey = false,
                WriteAddress = false
            }, ipAddress),
            dataDir);

    private sealed class StaticIpResolver(IPAddress address) : IIPResolver
    {
        public ValueTask<IIPResolver.NethermindIp> Resolve(CancellationToken cancellationToken = default) =>
            new(new IIPResolver.NethermindIp(address, address));
    }
}
