// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using FluentAssertions;
using FluentAssertions.Extensions;
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

public class E2EDiscoveryTests
{

    /// <summary>
    /// Common code for all node
    /// </summary>
    private IContainer CreateNode(PrivateKey nodeKey, IEnode? bootEnode = null)
    {
        IConfigProvider configProvider = new ConfigProvider();
        ChainSpec spec = new ChainSpecLoader(new EthereumJsonSerializer()).LoadEmbeddedOrFromFile("chainspec/foundation.json", default);
        spec.Bootnodes = [];

        INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
        networkConfig.DiscoveryPort = AssignDiscoveryPort();
        if (bootEnode is not null)
        {
            networkConfig.Bootnodes = bootEnode.ToString()!;
        }

        return new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(spec, configProvider, new TestLogManager()))
            .AddModule(new TestEnvironmentModule(nodeKey, $"{nameof(E2EDiscoveryTests)}"))
            .Build();
    }

    int _discoveryPort = 0;
    private int AssignDiscoveryPort()
    {
        return Interlocked.Increment(ref _discoveryPort);
    }

    [Test]
    public async Task TestDiscovery()
    {
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(10.Seconds());

        await using IContainer boot = CreateNode(TestItem.PrivateKeys[0]);
        IEnode bootEnode = boot.Resolve<IEnode>();
        await using IContainer node1 = CreateNode(TestItem.PrivateKeys[1], bootEnode);
        await using IContainer node2 = CreateNode(TestItem.PrivateKeys[2], bootEnode);
        await using IContainer node3 = CreateNode(TestItem.PrivateKeys[3], bootEnode);
        await using IContainer node4 = CreateNode(TestItem.PrivateKeys[4], bootEnode);

        IContainer[] nodes = [boot, node1, node2, node3, node4];

        HashSet<PublicKey> nodeKeys = nodes.Select(ctx => ctx.Resolve<IEnode>().PublicKey).ToHashSet();

        foreach (IContainer container in nodes)
        {
            await container.Resolve<PseudoNethermindRunner>().StartDiscovery(cancellationTokenSource.Token);
        }

        foreach (IContainer container in nodes)
        {
            var poolNodeSets = container.Resolve<IPeerPool>().Peers.Values.Select((p) => p.Node.Id).ToHashSet();
            poolNodeSets.Should().BeEquivalentTo(nodeKeys);
        }
    }
}
