// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using System.Net;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.Network.Enr;
using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class DiscoveryContainerTests
{
    [Test]
    public async Task Build_registers_bucket_sources_for_enabled_protocols()
    {
        string dataDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, dataDir);
        BootnodeKademliaBucketRegistry bucketRegistry = new();
        BootnodeOptions options = new()
        {
            DataDir = dataDir,
            DiscoveryPort = 30303,
            HttpHost = "127.0.0.1",
            HttpPort = 8546,
            MetricsHost = "127.0.0.1",
            MetricsPort = 6060,
            DiscoveryVersion = DiscoveryVersion.All,
            ActiveDiscovery = false,
            ActiveDiscoveryJobs = 0,
            BucketSize = 16,
            Concurrency = 3,
            DiscoveryIntervalMs = 30000,
            LocalIp = "127.0.0.1",
            ExternalIp = "127.0.0.1",
            ExternalIpV4 = null,
            ExternalIpV6 = null,
            Bootnodes = [],
            UseDefaultDiscv5Bootnodes = false,
            LogLevel = "Error",
            LogFile = null,
            PrivateKey = null,
            PrivateKeyFile = Path.Combine(dataDir, "bootnode.key"),
            GenKey = false,
            WriteAddress = false
        };

        await using IContainer container = await DiscoveryContainer.BuildAsync(
            options,
            LimboLogs.Instance,
            protectedPrivateKey,
            new ProcessExitSource(CancellationToken.None),
            bucketRegistry,
            CancellationToken.None);

        _ = container.Resolve<IDiscoveryApp>();
        BootnodeKademliaBucketSnapshot[] snapshot = bucketRegistry.CreateSnapshot();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot, Has.Some.Property(nameof(BootnodeKademliaBucketSnapshot.Protocol)).EqualTo("discv4"));
            Assert.That(snapshot, Has.Some.Property(nameof(BootnodeKademliaBucketSnapshot.Protocol)).EqualTo("discv5"));
        }
    }

    [Test]
    public async Task Build_publishes_dual_stack_enr_when_both_external_ips_are_configured()
    {
        string dataDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, dataDir);
        BootnodeKademliaBucketRegistry bucketRegistry = new();
        BootnodeOptions options = new()
        {
            DataDir = dataDir,
            DiscoveryPort = 30303,
            HttpHost = "127.0.0.1",
            HttpPort = 8546,
            MetricsHost = "127.0.0.1",
            MetricsPort = 6060,
            DiscoveryVersion = DiscoveryVersion.V5,
            ActiveDiscovery = false,
            ActiveDiscoveryJobs = 0,
            BucketSize = 16,
            Concurrency = 3,
            DiscoveryIntervalMs = 30000,
            LocalIp = "::",
            ExternalIp = null,
            ExternalIpV4 = "192.0.2.1",
            ExternalIpV6 = "2001:db8::1",
            Bootnodes = [],
            UseDefaultDiscv5Bootnodes = false,
            LogLevel = "Error",
            LogFile = null,
            PrivateKey = null,
            PrivateKeyFile = Path.Combine(dataDir, "bootnode.key"),
            GenKey = false,
            WriteAddress = false
        };

        await using IContainer container = await DiscoveryContainer.BuildAsync(
            options,
            LimboLogs.Instance,
            protectedPrivateKey,
            new ProcessExitSource(CancellationToken.None),
            bucketRegistry,
            CancellationToken.None);

        NodeRecord nodeRecord = await container.Resolve<INodeRecordProvider>().GetCurrentAsync();
        NodeRecord decoded = NodeRecord.FromEnrString(nodeRecord.ToString());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.GetObj<IPAddress>(EnrContentKey.Ip), Is.EqualTo(IPAddress.Parse("192.0.2.1")));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Udp), Is.EqualTo(30303));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Tcp), Is.Null);
            Assert.That(decoded.GetObj<IPAddress>(EnrContentKey.Ip6), Is.EqualTo(IPAddress.Parse("2001:db8::1")));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Udp6), Is.EqualTo(30303));
            Assert.That(decoded.GetValue<int>(EnrContentKey.Tcp6), Is.Null);
        }
    }
}
