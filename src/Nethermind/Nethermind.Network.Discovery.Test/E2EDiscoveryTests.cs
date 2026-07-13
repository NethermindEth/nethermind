// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

[TestFixture(DiscoveryVersion.V4)]
[TestFixture(DiscoveryVersion.V5)]
public class E2EDiscoveryTests(DiscoveryVersion discoveryVersion)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PeerDiscoveryTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PeerDiscoveryPollInterval = TimeSpan.FromMilliseconds(100);

    private IContainer CreateNode(PrivateKey nodeKey, IEnode? bootEnode = null)
    {
        ConfigProvider configProvider = new();
        ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
        ChainSpec spec = loader.LoadEmbeddedOrFromFile("chainspec/foundation.json");
        spec.Bootnodes = [];

        if (bootEnode is not null)
        {
            spec.Bootnodes = [new(bootEnode.PublicKey, bootEnode.HostIp.ToString(), bootEnode.Port)];
        }

        INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
        int port = AssignDiscoveryPort();
        networkConfig.LocalIp = networkConfig.ExternalIp = $"192.168.2.{AssignIp()}";
        networkConfig.DiscoveryPort = port;
        networkConfig.P2PPort = port;
        IDiscoveryConfig discoveryConfig = configProvider.GetConfig<IDiscoveryConfig>();
        discoveryConfig.DiscoveryVersion = discoveryVersion;
        discoveryConfig.UseDefaultDiscv5Bootnodes = false;

        IForkInfo forkInfo = Substitute.For<IForkInfo>();
        forkInfo.GetForkId(Arg.Any<ulong>(), Arg.Any<ulong>()).Returns(new ForkId(0, 0));
        forkInfo.IsForkIdCompatible(new ForkId(0, 0)).Returns(true);

        ContainerBuilder builder = new();
        builder
            .AddModule(new PseudoNethermindModule(spec, configProvider, new TestLogManager()))
            .AddModule(new TestEnvironmentModule(nodeKey, $"{nameof(E2EDiscoveryTests)}-{discoveryVersion}"));
        builder.RegisterInstance(forkInfo).As<IForkInfo>();
        return builder.Build();
    }

    int _discoveryPort = 0;
    private int AssignDiscoveryPort() => Interlocked.Increment(ref _discoveryPort);
    int _ip = 1;
    private int AssignIp() => Interlocked.Increment(ref _ip);

    [Test]
    [Category("Flaky"), Retry(3)]
    [Parallelizable(ParallelScope.None)]
    public async Task TestDiscovery()
    {
        using CancellationTokenSource cancellationTokenSource = new CancellationTokenSource().ThatCancelAfter(TestTimeout);

        await using IContainer boot = CreateNode(TestItem.PrivateKeys[0]);
        IEnode bootEnode = boot.Resolve<IEnode>();
        await using IContainer node1 = CreateNode(TestItem.PrivateKeys[1], bootEnode);
        await using IContainer node2 = CreateNode(TestItem.PrivateKeys[2], bootEnode);
        await using IContainer node3 = CreateNode(TestItem.PrivateKeys[3], bootEnode);
        await using IContainer node4 = CreateNode(TestItem.PrivateKeys[4], bootEnode);

        IContainer[] nodes = [boot, node1, node2, node3, node4];

        HashSet<PublicKey> nodeKeys = GetNodeKeys(nodes);

        foreach (IContainer node in nodes)
        {
            await node.Resolve<PseudoNethermindRunner>().StartDiscovery(cancellationTokenSource.Token);
        }

        Task[] waitTasks = new Task[nodes.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            waitTasks[i] = AssertPeerPoolContainsExpectedNodes(nodes[i], nodeKeys, cancellationTokenSource.Token);
        }

        await Task.WhenAll(waitTasks);
    }

    private static HashSet<PublicKey> GetNodeKeys(IContainer[] nodes)
    {
        HashSet<PublicKey> nodeKeys = [];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodeKeys.Add(nodes[i].Resolve<IEnode>().PublicKey);
        }

        return nodeKeys;
    }

    private static async Task AssertPeerPoolContainsExpectedNodes(IContainer node, HashSet<PublicKey> nodeKeys, CancellationToken cancellationToken)
    {
        IPeerPool pool = node.Resolve<IPeerPool>();
        PublicKey localKey = node.Resolve<IEnode>().PublicKey;
        HashSet<PublicKey> expectedKeys = [.. nodeKeys];
        expectedKeys.Remove(localKey);

        using CancellationTokenSource timeoutCts = new(PeerDiscoveryTimeout);
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        CancellationToken linkedToken = linkedCts.Token;

        while (!linkedToken.IsCancellationRequested)
        {
            HashSet<PublicKey> actualKeys = GetPeerKeys(pool);
            if (actualKeys.SetEquals(expectedKeys))
            {
                return;
            }

            try
            {
                await Task.Delay(PeerDiscoveryPollInterval, linkedToken);
            }
            catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
            {
                break;
            }
        }

        Assert.That(GetPeerKeys(pool), Is.EquivalentTo(expectedKeys), $"Node {localKey} did not discover all peers before {PeerDiscoveryTimeout}.");
    }

    private static HashSet<PublicKey> GetPeerKeys(IPeerPool pool)
    {
        HashSet<PublicKey> peerKeys = [];
        foreach (KeyValuePair<PublicKeyAsKey, Peer> peer in pool.Peers)
        {
            peerKeys.Add(peer.Value.Node.Id);
        }

        return peerKeys;
    }
}
