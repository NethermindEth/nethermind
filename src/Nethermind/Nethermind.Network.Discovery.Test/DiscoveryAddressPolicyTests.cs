// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4.Kademlia.Handlers;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Discovery.Discv5.Kademlia;
using Nethermind.Network.Discovery.Test.Discv5;
using Nethermind.Network.Enr;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class DiscoveryAddressPolicyTests
{
    [Test]
    public async Task Neighbors_follow_advertised_network_with_wildcard_listener(
        [Values("172.18.0.1", "8.8.8.8", "0.0.0.0", "127.0.0.2")] string external,
        [Values("10.1.2.3", "127.0.0.1", "169.254.1.2", "fc00::1", "9.9.9.9")] string target,
        [Values] bool privateInterface)
    {
        Node self = new(TestItem.PublicKeyA, external, 30303);
        Node mentioned = new(TestItem.PublicKeyB, target, 30303);
        using PeerAddressPolicy policy = new(new IIPResolver.NethermindIp(IPAddress.IPv6Any, IPAddress.Parse(external)),
            new LocalAddressSource(LimboLogs.Instance, () => [IPAddress.Loopback, IPAddress.Parse("169.254.1.2"), IPAddress.Parse("fe80::1"),
                IPAddress.Parse(privateInterface ? "172.18.0.1" : "9.9.9.9"), IPAddress.Parse("8.8.4.4")]));
        NeighbourMsgHandler handler = new(1, IPAddress.IPv6Any, policy.AllowedAddresses) { LocalNode = self };
        handler.Handle(new NeighborsMsg(TestItem.PublicKeyC, 0, new[] { mentioned }));
        Node[] actual = (await handler.TaskCompletionSource.Task).Value;
        bool acceptsPrivate = external == "172.18.0.1" || external == "0.0.0.0" && privateInterface;
        Assert.That(actual.Select(node => node.Id), Is.EqualTo(external == "127.0.0.2" || acceptsPrivate && target is "10.1.2.3" or "fc00::1" || target == "9.9.9.9" ? new[] { mentioned.Id } : []));
    }

    [TestCase("127.0.0.1", false)]
    [TestCase("127.0.0.1", true)]
    [TestCase("0.0.0.0", true)]
    [CancelAfter(10000)]
    public async Task Default_listener_dials_peer_learned_only_from_discv5(
        string external,
        bool bootnode,
        CancellationToken token)
    {
        using TcpListener remote = new(IPAddress.Loopback, 0);
        remote.Start();
        int port = ((IPEndPoint)remote.LocalEndpoint).Port;
        NodeRecord record = TestEnrBuilder.BuildSigned(TestItem.PrivateKeyB, IPAddress.Loopback, tcpPort: port, udpPort: port,
            configureExtras: enr => enr.SetEntry(new EthEntry(new ForkId(0x11111111, 0).HashBytes, 0)));
        Node discovered = new(TestItem.PublicKeyB, "127.0.0.1", port) { Enr = record, IsBootnode = bootnode };
        IKademlia<PublicKey, Node> table = Substitute.For<IKademlia<PublicKey, Node>>();
        table.IterateNodes().Returns(new[] { discovered });
        using PeerAddressPolicy policy = new(new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Parse(external)),
            new LocalAddressSource(LimboLogs.Instance, () => [IPAddress.Loopback, IPAddress.Parse("169.254.1.2"), IPAddress.Parse("fe80::1"),
                IPAddress.Parse("172.18.0.1"), IPAddress.Parse("9.9.9.9")]));
        NodeSource source = NodeSourceTests.CreateSource(table, new Node(TestItem.PublicKeyA, external, 30303), policy);
        await using IAsyncEnumerator<Node> candidates = source.DiscoverNodes(token).GetAsyncEnumerator(token);
        Assert.That(await candidates.MoveNextAsync(), Is.True);
        Node candidate = candidates.Current;
        Assert.That(candidate.IsBootnode, Is.EqualTo(bootnode));

        NetworkConfig config = new()
        {
            ExternalIp = external == "0.0.0.0" ? null : external,
            EnableExternalIpResolution = false,
            P2PPort = RlpxHostIntegrationTests.GetAvailablePort(),
            ProcessingThreadCount = 1,
            RlpxHostShutdownCloseTimeoutMs = 100
        };
        await using IPResolver resolver = new(config, LimboLogs.Instance);
        await using IContainer container = RlpxHostIntegrationTests.CreateContainer(config, resolver,
            localAddressSource: new LocalAddressSource(LimboLogs.Instance, () => [IPAddress.Loopback, IPAddress.Parse("169.254.1.2"), IPAddress.Parse("fe80::1"),
                IPAddress.Parse("172.18.0.1"), IPAddress.Parse("9.9.9.9")]));
        RlpxHost host = container.Resolve<RlpxHost>();
        try
        {
            await host.Init();
            Task<Socket> accept = remote.AcceptSocketAsync(token).AsTask();
            Assert.That(await host.ConnectAsync(candidate, token), Is.True);
            using Socket accepted = await accept;
            Assert.That(accepted.RemoteEndPoint, Is.Not.Null);
        }
        finally { await host.Shutdown(); }
    }
}
