// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Discovery.Discv4.Serializers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Bootnode.Test;

[TestFixture]
public class BootnodeDiscv4SerializersTests
{
    private const string PrivateKeyHex = "49a7b37aa6f6645917e7b807e9d1c00d4fa71f18343b0d4122a4d2df64dd6fee";

    [Test]
    public void Ping_endpoints_advertise_discovery_udp_and_zero_tcp()
    {
        using PrivateKey privateKey = CreatePrivateKey();
        IMessageSerializationService serializationService = CreateSerializationService(privateKey);
        PingMsg message = new(
            privateKey.PublicKey,
            expirationTime: 60,
            source: new IPEndPoint(IPAddress.Loopback, 30303),
            destination: new IPEndPoint(IPAddress.Parse("192.0.2.1"), 30304),
            new byte[Hash256.Size])
        {
            FarAddress = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 30304)
        };

        IByteBuffer buffer = serializationService.ZeroSerialize(message);
        int sourceUdpPort;
        int sourceTcpPort;
        int destinationUdpPort;
        int destinationTcpPort;
        try
        {
            RlpReader ctx = CreatePayloadContext(buffer);
            ctx.ReadSequenceLength();
            ctx.DecodeInt();

            ctx.ReadSequenceLength();
            ctx.DecodeByteArraySpan(RlpLimit.For<IPEndPoint>(16, nameof(IPEndPoint.Address)));
            sourceUdpPort = ctx.DecodeInt();
            sourceTcpPort = ctx.DecodeInt();

            ctx.ReadSequenceLength();
            ctx.DecodeByteArraySpan(RlpLimit.For<IPEndPoint>(16, nameof(IPEndPoint.Address)));
            destinationUdpPort = ctx.DecodeInt();
            destinationTcpPort = ctx.DecodeInt();
        }
        finally
        {
            buffer.Release();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sourceUdpPort, Is.EqualTo(30303));
            Assert.That(sourceTcpPort, Is.Zero);
            Assert.That(destinationUdpPort, Is.EqualTo(30304));
            Assert.That(destinationTcpPort, Is.Zero);
        }
    }

    [Test]
    public void Neighbors_advertises_zero_tcp_for_discovery_nodes()
    {
        using PrivateKey privateKey = CreatePrivateKey();
        IMessageSerializationService serializationService = CreateSerializationService(privateKey);
        Node localNode = new(privateKey.PublicKey, IPAddress.Loopback.ToString(), 30303);
        using PrivateKey remoteKey = new("3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266");
        Node remoteNode = new(remoteKey.PublicKey, "192.0.2.10", 30304);
        NeighborsMsg message = new(
            privateKey.PublicKey,
            expirationTime: 60,
            new[] { localNode, remoteNode })
        {
            FarAddress = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 30304)
        };

        IByteBuffer buffer = serializationService.ZeroSerialize(message);
        int localUdpPort;
        int localTcpPort;
        int remoteUdpPort;
        int remoteTcpPort;
        try
        {
            RlpReader ctx = CreatePayloadContext(buffer);
            ctx.ReadSequenceLength();
            int nodesEnd = ctx.ReadSequenceLength() + ctx.Position;

            (localUdpPort, localTcpPort) = DecodeNeighborPorts(ref ctx);
            (remoteUdpPort, remoteTcpPort) = DecodeNeighborPorts(ref ctx);
            ctx.Check(nodesEnd);
        }
        finally
        {
            buffer.Release();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(localUdpPort, Is.EqualTo(30303));
            Assert.That(localTcpPort, Is.Zero);
            Assert.That(remoteUdpPort, Is.EqualTo(30304));
            Assert.That(remoteTcpPort, Is.Zero);
        }
    }

    private static PrivateKey CreatePrivateKey() => new(PrivateKeyHex);

    private static IMessageSerializationService CreateSerializationService(PrivateKey privateKey)
    {
        Ecdsa ecdsa = new();
        SameKeyGenerator privateKeyProvider = new(privateKey);
        NodeIdResolver nodeIdResolver = new(ecdsa);

        return new MessageSerializationService(
            SerializerInfo.Create(new BootnodePingMsgSerializer(ecdsa, privateKeyProvider, nodeIdResolver)),
            SerializerInfo.Create(new PongMsgSerializer(ecdsa, privateKeyProvider, nodeIdResolver)),
            SerializerInfo.Create(new FindNodeMsgSerializer(ecdsa, privateKeyProvider, nodeIdResolver)),
            SerializerInfo.Create(new BootnodeNeighborsMsgSerializer(ecdsa, privateKeyProvider, nodeIdResolver)),
            SerializerInfo.Create(new EnrRequestMsgSerializer(ecdsa, privateKeyProvider, nodeIdResolver)),
            SerializerInfo.Create(new EnrResponseMsgSerializer(ecdsa, privateKeyProvider, nodeIdResolver)));
    }

    private static RlpReader CreatePayloadContext(IByteBuffer buffer)
    {
        byte[] packet = buffer.ReadAllBytesAsArray();
        return new RlpReader(packet.AsMemory((32 + 64 + 1 + 1)..));
    }

    private static (int udpPort, int tcpPort) DecodeNeighborPorts(ref RlpReader ctx)
    {
        int nodeEnd = ctx.ReadSequenceLength() + ctx.Position;
        ctx.DecodeByteArraySpan(RlpLimit.For<IPEndPoint>(16, nameof(IPEndPoint.Address)));
        int udpPort = ctx.DecodeInt();
        int tcpPort = ctx.DecodeInt();
        ctx.DecodeByteArraySpan(RlpLimit.L64);
        ctx.Check(nodeEnd);
        return (udpPort, tcpPort);
    }
}
