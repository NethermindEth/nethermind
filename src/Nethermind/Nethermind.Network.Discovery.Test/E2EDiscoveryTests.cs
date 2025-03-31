// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

[TestFixture(DiscoveryVersion.V4)]
[TestFixture(DiscoveryVersion.V5)]
public class E2EDiscoveryTests(DiscoveryVersion discoveryVersion)
{
    private static TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Common code for all node
    /// </summary>
    private IContainer CreateNode(PrivateKey nodeKey, IEnode? bootEnode = null)
    {
        IConfigProvider configProvider = new ConfigProvider();
        var loader = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboTraceLogger.Instance);
        ChainSpec spec = loader.LoadEmbeddedOrFromFile("chainspec/foundation.json");
        spec.Bootnodes = [];
        if (bootEnode is not null)
        {
            spec.Bootnodes = [new(bootEnode.PublicKey, bootEnode.HostIp.ToString(), bootEnode.Port)];
        }

        INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
        int port = AssignDiscoveryPort();
        networkConfig.LocalIp = networkConfig.ExternalIp = $"192.168.2.{AssignDiscoveryIp()}";
        networkConfig.DiscoveryPort = port;
        networkConfig.P2PPort = port;

        IDiscoveryConfig discoveryConfig = configProvider.GetConfig<IDiscoveryConfig>();
        discoveryConfig.DiscoveryVersion = discoveryVersion;

        return new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(spec, configProvider, new TestLogManager()))
            .AddModule(new TestEnvironmentModule(nodeKey, $"{nameof(E2EDiscoveryTests)}-{discoveryVersion}"))
            .Build();
    }

    int _discoveryPort = 0;
    private int AssignDiscoveryPort()
    {
        return Interlocked.Increment(ref _discoveryPort);
    }
    int _discoveryIp = 1;
    private int AssignDiscoveryIp()
    {
        return Interlocked.Increment(ref _discoveryIp);
    }

    [Test]
    public async Task TestDiscovery()
    {
        if (discoveryVersion == DiscoveryVersion.V5) Assert.Ignore("DiscV5 does not seems to work.");
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);

        await using IContainer boot = CreateNode(TestItem.PrivateKeys[0]);
        IEnode bootEnode = boot.Resolve<IEnode>();
        await using IContainer node1 = CreateNode(TestItem.PrivateKeys[1], bootEnode);
        await using IContainer node2 = CreateNode(TestItem.PrivateKeys[2], bootEnode);
        await using IContainer node3 = CreateNode(TestItem.PrivateKeys[3], bootEnode);
        await using IContainer node4 = CreateNode(TestItem.PrivateKeys[4], bootEnode);

        IContainer[] nodes = [boot, node1, node2, node3, node4];

        HashSet<PublicKey> nodeKeys = nodes.Select(ctx => ctx.Resolve<IEnode>().PublicKey).ToHashSet();

        foreach (IContainer node in nodes)
        {
            await node.Resolve<PseudoNethermindRunner>().StartDiscovery(cancellationTokenSource.Token);
        }

        foreach (IContainer node in nodes)
        {
            IPeerPool pool = node.Resolve<IPeerPool>();
            HashSet<PublicKey> expectedKeys = new HashSet<PublicKey>(nodeKeys);
            expectedKeys.Remove(node.Resolve<IEnode>().PublicKey);

            Assert.That(() => pool.Peers.Values.Select((p) => p.Node.Id).ToHashSet(), Is.EquivalentTo(expectedKeys).After(1000, 100));
        }
    }
}
