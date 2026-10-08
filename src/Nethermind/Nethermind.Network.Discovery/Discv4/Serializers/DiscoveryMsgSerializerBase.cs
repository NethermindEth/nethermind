// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    protected const int MdcSigOffset = 32 + 64 + 1;
    protected const int EnvelopeLength = MdcSigOffset + 1;

    protected static void PrepareBufferForSerialization(Span<byte> buffer, byte msgType) =>
        // [<mdc 32 Bytes><sig 64 Bytes><SigRecoveryId><MsgType><Data>]
        buffer[MdcSigOffset] = msgType;

    protected void AddSignatureAndMdc(Span<byte> buffer)
    {
        // [<mdc 32 Bytes><sig 64 Bytes><SigRecoveryId><MsgType><Data>]
        ValueHash256 toSign = ValueKeccak.Compute(buffer.Slice(MdcSigOffset));
        Signature signature = _ecdsa.Sign(_privateKey, in toSign);
        signature.Bytes.CopyTo(buffer.Slice(32, 64));
        buffer[96] = signature.RecoveryId;

        ValueHash256 mdc = ValueKeccak.Compute(buffer.Slice(32));
        mdc.BytesAsSpan.CopyTo(buffer);
    }

    protected (PublicKey FarPublicKey, ValueHash256 Mdc) PrepareForDeserialization(ReadOnlySpan<byte> msg)
    {
        if (msg.Length < EnvelopeLength)
        {
            throw new NetworkingException("Incorrect message", NetworkExceptionType.Validation);
        }
        ValueHash256 mdc = new(msg[..Hash256.Size]);
        ReadOnlySpan<byte> sigAndData = msg[32..];
        ValueHash256 computedMdc = ValueKeccak.Compute(sigAndData);

        if (!Bytes.AreEqual(mdc.BytesAsSpan, computedMdc.BytesAsSpan))
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
