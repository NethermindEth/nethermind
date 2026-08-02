// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Config;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Discovery;
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
}
