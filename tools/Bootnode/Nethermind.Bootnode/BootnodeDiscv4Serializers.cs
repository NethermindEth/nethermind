// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using Autofac.Features.AttributeFilters;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Discovery.Discv4.Serializers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Bootnode;

internal sealed class BootnodePingMsgSerializer(
    IEcdsa ecdsa,
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey,
    INodeIdResolver nodeIdResolver)
    : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IZeroInnerMessageSerializer<PingMsg>
{
    public void Serialize(IByteBuffer byteBuffer, PingMsg msg)
    {
        (int totalLength, int contentLength, int sourceAddressLength, int destinationAddressLength) = GetLength(msg);

        byteBuffer.MarkIndex();
        PrepareBufferForSerialization(byteBuffer, totalLength, (byte)msg.MsgType);
        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);
        stream.Encode(msg.Version);
        EncodeEndpoint(stream, msg.SourceAddress, sourceAddressLength, tcpPort: 0);
        EncodeEndpoint(stream, msg.DestinationAddress, destinationAddressLength, tcpPort: 0);
        stream.Encode(msg.ExpirationTime);

        if (msg.EnrSequence.HasValue)
        {
            stream.Encode(msg.EnrSequence.Value);
        }

        byteBuffer.ResetIndex();
        AddSignatureAndMdc(byteBuffer, totalLength + 1);

        byteBuffer.MarkReaderIndex();
        msg.Mdc = ReadHash(byteBuffer, byteBuffer.ReaderIndex);
        byteBuffer.ResetReaderIndex();
    }

    public PingMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey farPublicKey, ValueHash256 mdc, IByteBuffer data) = PrepareForDeserialization(msgBytes);
        Rlp.ValueDecoderContext ctx = data.AsRlpContext();
        ctx.ReadSequenceLength();
        int version = ctx.DecodeInt();

        ctx.ReadSequenceLength();
        ReadOnlySpan<byte> sourceAddress = ctx.DecodeByteArraySpan(IpAddressRlpLimit);
        int sourceUdpPort = ctx.DecodeInt();
        ctx.DecodeInt();
        IPEndPoint source = GetAddress(sourceAddress, sourceUdpPort, allowZeroPort: true);

        ctx.ReadSequenceLength();
        ReadOnlySpan<byte> destinationAddress = ctx.DecodeByteArraySpan(IpAddressRlpLimit);
        int destinationUdpPort = ctx.DecodeInt();
        ctx.DecodeInt();
        IPEndPoint destination = GetAddress(destinationAddress, destinationUdpPort, allowZeroPort: true);

        long expireTime = ctx.DecodeLong();
        PingMsg msg = new(farPublicKey, expireTime, source, destination, mdc) { Version = version };

        if (version == 4 && ctx.Position < ctx.Length)
        {
            msg.EnrSequence = ctx.DecodeULong();
        }

        data.SetReaderIndex(data.ReaderIndex + ctx.Position);
        return msg;
    }

    public int GetLength(PingMsg msg, out int contentLength)
    {
        (int totalLength, contentLength, int _, int _) = GetLength(msg);
        return totalLength;
    }

    private static (int totalLength, int contentLength, int sourceAddressLength, int destinationAddressLength) GetLength(PingMsg msg)
    {
        int sourceAddressLength = GetEndpointLength(msg.SourceAddress, tcpPort: 0);
        int destinationAddressLength = GetEndpointLength(msg.DestinationAddress, tcpPort: 0);

        int contentLength = Rlp.LengthOf(msg.Version)
            + Rlp.LengthOfSequence(sourceAddressLength)
            + Rlp.LengthOfSequence(destinationAddressLength)
            + Rlp.LengthOf(msg.ExpirationTime);

        if (msg.EnrSequence.HasValue)
        {
            contentLength += Rlp.LengthOf(msg.EnrSequence.Value);
        }

        return (Rlp.LengthOfSequence(contentLength), contentLength, sourceAddressLength, destinationAddressLength);
    }

    private static void EncodeEndpoint(RlpStream stream, IPEndPoint address, int length, int tcpPort)
    {
        stream.StartSequence(length);
        EncodeIpAddress(stream, address.Address);
        stream.Encode(address.Port);
        stream.Encode(tcpPort);
    }

    private static int GetEndpointLength(IPEndPoint address, int tcpPort)
        => GetIpAddressLength(address.Address) + Rlp.LengthOf(address.Port) + Rlp.LengthOf(tcpPort);

    private static int GetIpAddressLength(IPAddress address)
        => address.AddressFamily switch
        {
            AddressFamily.InterNetwork => Rlp.LengthOfByteString(4, 0),
            AddressFamily.InterNetworkV6 => Rlp.LengthOfByteString(16, 0),
            _ => Rlp.LengthOf(address.GetAddressBytes())
        };

    private static void EncodeIpAddress(RlpStream stream, IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (address.TryWriteBytes(bytes, out int bytesWritten))
        {
            stream.Encode(bytes[..bytesWritten]);
            return;
        }

        stream.Encode(address.GetAddressBytes());
    }
}

internal sealed class BootnodeNeighborsMsgSerializer(
    IEcdsa ecdsa,
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey,
    INodeIdResolver nodeIdResolver)
    : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IZeroInnerMessageSerializer<NeighborsMsg>
{
    private static readonly RlpLimit NodesRlpLimit = RlpLimit.For<NeighborsMsg>(16, nameof(NeighborsMsg.Nodes));

    public void Serialize(IByteBuffer byteBuffer, NeighborsMsg msg)
    {
        (int totalLength, int contentLength, int nodesContentLength) = GetLength(msg);

        byteBuffer.MarkIndex();
        PrepareBufferForSerialization(byteBuffer, totalLength, (byte)msg.MsgType);
        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);
        if (msg.Nodes.Count != 0)
        {
            stream.StartSequence(nodesContentLength);
            for (int i = 0; i < msg.Nodes.Count; i++)
            {
                Node node = msg.Nodes[i];
                SerializeNode(stream, node.Address, node.Id.Bytes, tcpPort: 0);
            }
        }
        else
        {
            stream.Encode(Rlp.OfEmptyList);
        }

        stream.Encode(msg.ExpirationTime);
        byteBuffer.ResetIndex();

        AddSignatureAndMdc(byteBuffer, totalLength + 1);
    }

    public NeighborsMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey farPublicKey, _, IByteBuffer data) = PrepareForDeserialization(msgBytes);

        Rlp.ValueDecoderContext ctx = data.AsRlpContext();
        ctx.ReadSequenceLength();
        int nodesEnd = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(nodesEnd);
        ctx.GuardLimit(count, NodesRlpLimit);
        Node[] decoded = new Node[count];
        int nodeCount = 0;
        for (int i = 0; i < count; i++)
        {
            if (ctx.IsNextItemEmptyList())
            {
                ctx.SkipItem();
                continue;
            }

            decoded[nodeCount++] = DecodeNode(ref ctx);
        }

        ctx.Check(nodesEnd);
        if (nodeCount != decoded.Length)
        {
            Array.Resize(ref decoded, nodeCount);
        }

        long expirationTime = ctx.DecodeLong();
        data.SetReaderIndex(data.ReaderIndex + ctx.Position);
        return new NeighborsMsg(farPublicKey, expirationTime, decoded);
    }

    public int GetLength(NeighborsMsg msg, out int contentLength)
    {
        (int totalLength, contentLength, int _) = GetLength(msg);
        return totalLength;
    }

    private static Node DecodeNode(ref Rlp.ValueDecoderContext ctx)
    {
        int lastPosition = ctx.ReadSequenceLength() + ctx.Position;
        int count = ctx.PeekNumberOfItemsRemaining(lastPosition);
        ReadOnlySpan<byte> ip = ctx.DecodeByteArraySpan(IpAddressRlpLimit);
        IPEndPoint address = GetAddress(ip, ctx.DecodeInt());
        if (count > 3)
        {
            ctx.DecodeInt();
        }

        ReadOnlySpan<byte> id = ctx.DecodeByteArraySpan(NodeIdRlpLimit);
        return new Node(new PublicKey(id), address);
    }

    private static (int totalLength, int contentLength, int nodesContentLength) GetLength(NeighborsMsg msg)
    {
        int nodesContentLength = 0;
        int contentLength = 0;
        if (msg.Nodes.Count != 0)
        {
            contentLength += GetNodesLength(msg.Nodes, out nodesContentLength);
        }
        else
        {
            contentLength += Rlp.OfEmptyList.Bytes.Length;
        }

        contentLength += Rlp.LengthOf(msg.ExpirationTime);

        return (Rlp.LengthOfSequence(contentLength), contentLength, nodesContentLength);
    }

    private static int GetNodesLength(ArraySegment<Node> nodes, out int contentLength)
    {
        contentLength = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            Node node = nodes[i];
            contentLength += Rlp.LengthOfSequence(GetLengthSerializeNode(node.Address, node.Id.Bytes, tcpPort: 0));
        }

        return Rlp.LengthOfSequence(contentLength);
    }

    private static void SerializeNode(RlpStream stream, IPEndPoint address, byte[] id, int tcpPort)
    {
        int length = GetLengthSerializeNode(address, id, tcpPort);
        stream.StartSequence(length);
        EncodeIpAddress(stream, address.Address);
        stream.Encode(address.Port);
        stream.Encode(tcpPort);
        stream.Encode(id);
    }

    private static int GetLengthSerializeNode(IPEndPoint address, byte[] id, int tcpPort)
        => GetIpAddressLength(address.Address) + Rlp.LengthOf(address.Port) + Rlp.LengthOf(tcpPort) + Rlp.LengthOf(id);

    private static int GetIpAddressLength(IPAddress address)
        => address.AddressFamily switch
        {
            AddressFamily.InterNetwork => Rlp.LengthOfByteString(4, 0),
            AddressFamily.InterNetworkV6 => Rlp.LengthOfByteString(16, 0),
            _ => Rlp.LengthOf(address.GetAddressBytes())
        };

    private static void EncodeIpAddress(RlpStream stream, IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (address.TryWriteBytes(bytes, out int bytesWritten))
        {
            stream.Encode(bytes[..bytesWritten]);
            return;
        }

        stream.Encode(address.GetAddressBytes());
    }
}
