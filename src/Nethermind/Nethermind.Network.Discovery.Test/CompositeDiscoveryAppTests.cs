// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test;

public class CompositeDiscoveryAppTests
{
    [TestCase("0.0.0.0", AddressFamily.InterNetwork, false)]
    [TestCase("127.0.0.1", AddressFamily.InterNetwork, false)]
    [TestCase("::1", AddressFamily.InterNetworkV6, false)]
    [TestCase("::", AddressFamily.InterNetworkV6, true)]
    [TestCase("::ffff:0.0.0.0", AddressFamily.InterNetworkV6, true)]
    public void CreateDatagramSocket_MatchesListenerAddress(string localIp, AddressFamily expectedFamily, bool expectedDualMode)
    {
        if (localIp == "::" && OperatingSystem.IsMacOS())
        {
            expectedDualMode = false;
        }

        using Socket socket = CompositeDiscoveryApp.CreateDatagramSocket(IPAddress.Parse(localIp));

        Assert.That(socket.AddressFamily, Is.EqualTo(expectedFamily));
        if (expectedFamily == AddressFamily.InterNetworkV6)
        {
            Assert.That(socket.DualMode, Is.EqualTo(expectedDualMode));
        }
    }

    [Test]
    [NonParallelizable]
    public async Task StartAsync_ReleasesChannelAndEventLoopWhenBindFails()
    {
        int port;
        using (Socket blocker = CreateUdpListenerSocket(0))
        {
            port = ((IPEndPoint)blocker.LocalEndPoint!).Port;
            NetworkConfig networkConfig = new() { LocalIp = "0.0.0.0", DiscoveryPort = port };
            IIPResolver ipResolver = Substitute.For<IIPResolver>();
            ipResolver.Resolve(Arg.Any<CancellationToken>()).Returns(new ValueTask<IIPResolver.NethermindIp>(
                new IIPResolver.NethermindIp(IPAddress.Any, IPAddress.Loopback)));
            NetworkListenerState listenerState = new(networkConfig, ipResolver, LimboLogs.Instance);
            IDiscoveryApp discoveryApp = Substitute.For<IDiscoveryApp>();
            discoveryApp.StopAsync().Returns(Task.CompletedTask);
            RecordingChannelFactory channelFactory = new();
            CompositeDiscoveryApp app = new(
                networkConfig,
                new DiscoveryConfig(),
                LimboLogs.Instance,
                listenerState,
                [discoveryApp],
                channelFactory);

            Assert.That(async () => await app.StartAsync(), Throws.TypeOf<PortInUseException>());

            Assert.That(app.HasEventLoopGroup, Is.False);
            Assert.That(listenerState.DiscoveryAddress, Is.Null);
            Assert.That(channelFactory.CreatedChannels, Has.Count.EqualTo(1));
            Assert.That(channelFactory.CreatedChannels[0].Open, Is.False);
            Assert.That(channelFactory.CreatedChannels[0].CloseCompletion.IsCompletedSuccessfully, Is.True);
            await discoveryApp.Received(1).StopAsync();
        }

        using Socket released = CreateUdpListenerSocket(port);
    }

    [TestCase("0.0.0.0", "2001:db8::1", "192.0.2.1", 30304)]
    [TestCase("2001:db8::5", "192.0.2.1", "2001:db8::1", 30305)]
    [TestCase("::", "2001:db8::1", "2001:db8::1", 30305)]
    public void TryCreateReachableDiscoveryNode_SelectsReachableFamily(
        string localIp,
        string preferredIp,
        string expectedIp,
        int expectedDiscoveryPort)
    {
        NodeRecord record = CreateDualStackRecord();

        bool result = CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(
            record,
            IPAddress.Parse(localIp),
            new IPEndPoint(IPAddress.Parse(preferredIp), 40404),
            out Node? node);

        Assert.That(result, Is.True);
        Assert.That(node, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(node!.Host, Is.EqualTo(expectedIp));
            Assert.That(node.DiscoveryPort, Is.EqualTo(expectedDiscoveryPort));
        }
    }

    [Test]
    public void TryCreateReachableDiscoveryNode_RejectsFamilyOutsideListener()
    {
        NodeRecord record = new();
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
        record.SetEntry(new Udp6Entry(30305));

        bool result = CompositeDiscoveryApp.TryCreateReachableDiscoveryNode(
            record,
            IPAddress.Any,
            preferredEndpoint: null,
            out Node? node);

        Assert.That(result, Is.False);
        Assert.That(node, Is.Null);
    }

    private static NodeRecord CreateDualStackRecord()
    {
        NodeRecord record = new();
        record.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        record.SetEntry(new IpEntry(IPAddress.Parse("192.0.2.1")));
        record.SetEntry(new TcpEntry(30303));
        record.SetEntry(new UdpEntry(30304));
        record.SetEntry(new Ip6Entry(IPAddress.Parse("2001:db8::1")));
        record.SetEntry(new Tcp6Entry(30306));
        record.SetEntry(new Udp6Entry(30305));
        return record;
    }

    private static Socket CreateUdpListenerSocket(int port)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = true
        };
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        return socket;
    }

    private sealed class RecordingChannelFactory : IChannelFactory
    {
        public List<IChannel> CreatedChannels { get; } = [];

        public IChannel CreateDatagramChannel()
        {
            IChannel channel = new SocketDatagramChannel(CompositeDiscoveryApp.CreateDatagramSocket(IPAddress.Any));
            CreatedChannels.Add(channel);
            return channel;
        }

        public IServerChannel CreateServer() => throw new NotSupportedException();

        public IChannel CreateClient() => throw new NotSupportedException();
    }
}
