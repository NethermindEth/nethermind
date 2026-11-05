// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public abstract class DiscoveryMsgSerializerBase(IEcdsa ecdsa,
    IPrivateKeyGenerator nodeKey,
    INodeIdResolver nodeIdResolver)
{
    protected static readonly RlpLimit IpAddressRlpLimit = RlpLimit.For<IPEndPoint>(16, nameof(IPEndPoint.Address));
    protected static readonly RlpLimit NodeIdRlpLimit = RlpLimit.L64;
    private readonly PrivateKey _privateKey = nodeKey.Generate();
    protected readonly IEcdsa _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));

    private readonly INodeIdResolver _nodeIdResolver = nodeIdResolver ?? throw new ArgumentNullException(nameof(nodeIdResolver));

    // [<mdc 32 Bytes><sig 64 Bytes><SigRecoveryId><MsgType><Data>]
    protected const int MdcSigOffset = 32 + 64 + 1;
    private const int DataOffset = MdcSigOffset + 1;

    /// <summary>
    /// Gets the length of a packet whose RLP data is <paramref name="dataLength"/> bytes long.
    /// </summary>
    protected static int GetPacketLength(int dataLength) => DataOffset + dataLength;

    /// <summary>
    /// Writes the message type into <paramref name="packet"/> and returns a writer for the RLP data that follows it.
    /// </summary>
    protected static RlpWriter StartPacket(Span<byte> packet, byte msgType)
    {
        packet[MdcSigOffset] = msgType;
        return new RlpWriter(packet[DataOffset..]);
    }

    /// <summary>
    /// Signs the packet whose RLP data <paramref name="writer"/> has written and writes its hash in front.
    /// </summary>
    /// <param name="mdc">The packet hash, which identifies the message.</param>
    /// <returns>The packet length.</returns>
    protected int CompletePacket(Span<byte> packet, in RlpWriter writer, out ValueHash256 mdc)
    {
        packet = packet[..GetPacketLength(writer.Position)];

        ValueHash256 toSign = ValueKeccak.Compute(packet[MdcSigOffset..]);
        Signature signature = _ecdsa.Sign(_privateKey, in toSign);
        signature.Bytes.CopyTo(packet[Hash256.Size..]);
        packet[MdcSigOffset - 1] = signature.RecoveryId;

        mdc = ValueKeccak.Compute(packet[Hash256.Size..]);
        mdc.Bytes.CopyTo(packet);
        return packet.Length;
    }

    protected (PublicKey FarPublicKey, ValueHash256 Mdc) PrepareForDeserialization(ReadOnlySpan<byte> msg, out ReadOnlySpan<byte> data)
    {
        if (msg.Length < DataOffset)
        {
            throw new NetworkingException("Incorrect message", NetworkExceptionType.Validation);
        }

        data = msg[DataOffset..];
        ValueHash256 mdc = new(msg[..Hash256.Size]);
        ReadOnlySpan<byte> sigAndData = msg[Hash256.Size..];
        ValueHash256 computedMdc = ValueKeccak.Compute(sigAndData);

        if (mdc != computedMdc)
        {
            throw new NetworkingException("Invalid packet hash", NetworkExceptionType.Validation);
        }

        PublicKey nodeId = _nodeIdResolver.GetNodeId(sigAndData[..64], sigAndData[64], sigAndData[65..]);
        return (nodeId, mdc);
    }

    protected static bool IsNextEnrSequence(RlpReader ctx)
    {
        byte prefix = ctx.PeekByte();
        return prefix switch
        {
            >= 1 and < 128 or 128 => true,
            > 128 and <= 136 => IsCanonicalMultiByteInteger(ctx, prefix - 128),
            _ => false
        };
    }

    private static bool IsCanonicalMultiByteInteger(RlpReader ctx, int length)
    {
        if (length >= ctx.Length - ctx.Position)
        {
            throw new RlpException("Truncated discovery ENR sequence.");
        }

        byte firstByte = ctx.Peek(1, 1)[0];
        return length == 1 ? firstByte >= 128 : firstByte != 0;
    }

    protected static void Encode<TWriter>(ref TWriter writer, IPEndPoint address, int length)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
        => Encode(ref writer, address, address.Port, length);

    protected static void Encode<TWriter>(ref TWriter writer, IPEndPoint address, int tcpPort, int length)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(length);
        IPAddressRlp.Encode(ref writer, address.Address);
        writer.Encode(address.Port);
        writer.Encode(tcpPort);
    }

    protected static int GetIPEndPointLength(IPEndPoint address) => GetIPEndPointLength(address, address.Port);

    protected static int GetIPEndPointLength(IPEndPoint address, int tcpPort)
    {
        int length = IPAddressRlp.GetLength(address.Address);
        length += Rlp.LengthOf(address.Port);
        length += Rlp.LengthOf(tcpPort);
        return length;
    }

    protected static void SerializeNode<TWriter>(ref TWriter writer, Node node)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int length = GetLengthSerializeNode(node);
        writer.StartSequence(length);
        IPAddressRlp.Encode(ref writer, node.Address.Address);
        writer.Encode(node.DiscoveryPort);
        writer.Encode(GetSerializedTcpPort(node));
        writer.Encode(node.Id.Bytes);
    }

    protected static int GetLengthSerializeNode(Node node)
    {
        int tcpPort = GetSerializedTcpPort(node);
        int length = IPAddressRlp.GetLength(node.Address.Address);
        length += Rlp.LengthOf(node.DiscoveryPort);
        length += Rlp.LengthOf(tcpPort);
        length += Rlp.LengthOf(node.Id.Bytes);
        return length;
    }

    private static int GetSerializedTcpPort(Node node) => node.Port;

    protected static IPEndPoint GetAddress(ReadOnlySpan<byte> ip, int port, bool allowZeroPort = false)
    {
        if (allowZeroPort ? (uint)port > ushort.MaxValue : (uint)(port - 1) >= ushort.MaxValue)
        {
            ThrowInvalidPort(port);
        }

        if (ip.Length is not (4 or 16))
        {
            ThrowInvalidIP(ip);
        }

        return new IPEndPoint(new IPAddress(ip).NormalizeMappedIPv4(), port);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidPort(int port) => throw new NetworkingException($"Invalid discovery port {port}.", NetworkExceptionType.Validation);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidIP(ReadOnlySpan<byte> ip) => throw new NetworkingException($"Invalid discovery IP {ip.ToHexString()}.", NetworkExceptionType.Validation);
    }
}
