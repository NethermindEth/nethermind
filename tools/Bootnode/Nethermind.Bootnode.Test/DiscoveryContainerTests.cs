// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using System.Net;
using System.Net.Sockets;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.Network.Enr;
using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
[NonParallelizable]
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
        BootnodeOptions options = CreateOptions(
            dataDir,
            discoveryPort: 30303,
            DiscoveryVersion.All,
            localIp: "127.0.0.1",
            externalIp: "127.0.0.1");

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
    public async Task Default_bind_publishes_only_actual_listener_families()
    {
        string dataDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        using PrivateKeyGenerator generator = new();
        using PrivateKey privateKey = generator.Generate();
        IProtectedPrivateKey protectedPrivateKey = new ProtectedPrivateKey(privateKey, dataDir);
        BootnodeKademliaBucketRegistry bucketRegistry = new();
        int discoveryPort = GetAvailableUdpPort();
        BootnodeOptions options = CreateOptions(
            dataDir,
            discoveryPort,
            DiscoveryVersion.V5,
            localIp: null,
            externalIpV4: "192.0.2.1",
            externalIpV6: "2001:db8::1");

        await using IContainer container = await DiscoveryContainer.BuildAsync(
            options,
            LimboLogs.Instance,
            protectedPrivateKey,
            new ProcessExitSource(CancellationToken.None),
            bucketRegistry,
            CancellationToken.None);

        IDiscoveryApp discoveryApp = container.Resolve<IDiscoveryApp>();
        bool started = false;
        try
        {
            await discoveryApp.StartAsync();
            started = true;

            NodeRecord decoded = await GetDecodedNodeRecord(container);
            bool supportsIpv6 = Socket.OSSupportsIPv6 && !OperatingSystem.IsMacOS();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(container.Resolve<NetworkListenerState>().DiscoveryAddress, Is.EqualTo(supportsIpv6 ? IPAddress.IPv6Any : IPAddress.Any));
                Assert.That(decoded.GetObj<IPAddress>(EnrContentKey.Ip), Is.EqualTo(IPAddress.Parse("192.0.2.1")));
                Assert.That(decoded.GetValue<int>(EnrContentKey.Udp), Is.EqualTo(discoveryPort));
                Assert.That(decoded.GetValue<int>(EnrContentKey.Tcp), Is.Null);
                Assert.That(decoded.GetObj<IPAddress>(EnrContentKey.Ip6), Is.EqualTo(supportsIpv6 ? IPAddress.Parse("2001:db8::1") : null));
                Assert.That(decoded.GetValue<int>(EnrContentKey.Udp6), Is.EqualTo(supportsIpv6 ? discoveryPort : null));
                Assert.That(decoded.GetValue<int>(EnrContentKey.Tcp6), Is.Null);
            }
        }
        finally
        {
            if (started)
            {
                await discoveryApp.StopAsync();
            }
        }
    }

    private static async Task<NodeRecord> GetDecodedNodeRecord(IContainer container)
    {
        NodeRecord nodeRecord = await container.Resolve<INodeRecordProvider>().GetCurrentAsync();
        return NodeRecord.FromEnrString(nodeRecord.ToString());
    }

    private static BootnodeOptions CreateOptions(
        string dataDir,
        int discoveryPort,
        DiscoveryVersion discoveryVersion,
        string? localIp,
        string? externalIp = null,
        string? externalIpV4 = null,
        string? externalIpV6 = null)
        => new()
        {
            DataDir = dataDir,
            DiscoveryPort = discoveryPort,
            HttpHost = "127.0.0.1",
            HttpPort = 8546,
            MetricsHost = "127.0.0.1",
            MetricsPort = 6060,
            DiscoveryVersion = discoveryVersion,
            ActiveDiscovery = false,
            ActiveDiscoveryJobs = 0,
            BucketSize = 16,
            Concurrency = 3,
            DiscoveryIntervalMs = 30000,
            LocalIp = localIp,
            ExternalIp = externalIp,
            ExternalIpV4 = externalIpV4,
            ExternalIpV6 = externalIpV6,
            Bootnodes = [],
            UseDefaultDiscv5Bootnodes = false,
            LogLevel = "Error",
            LogFile = null,
            PrivateKey = null,
            PrivateKeyFile = Path.Combine(dataDir, "bootnode.key"),
            GenKey = false,
            WriteAddress = false
        };

    private static int GetAvailableUdpPort()
    {
        using Socket socket = CreateUdpListenerSocket(IPAddress.Any, 0);
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static Socket CreateUdpListenerSocket(IPAddress address, int port)
    {
        Socket socket = new(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ExclusiveAddressUse = true;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = false;
            }

            socket.Bind(new IPEndPoint(address, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

}
