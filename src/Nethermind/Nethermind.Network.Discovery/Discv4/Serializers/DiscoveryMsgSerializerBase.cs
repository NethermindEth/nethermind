// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Serialization.Rlp;

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

    protected void Serialize(byte type, Span<byte> data, IByteBuffer byteBuffer)
    {
        // [<mdc 32 Bytes><sig 64 Bytes><SigRecoveryId><MsgType><Data>]
        int length = 32 + 1 + data.Length + 64 + 1;
        byteBuffer.EnsureWritable(length);

        int startReadIndex = byteBuffer.ReaderIndex;
        int startWriteIndex = byteBuffer.WriterIndex;

        byteBuffer.SetWriterIndex(startWriteIndex + 32 + 65);
        byteBuffer.WriteByte(type);
        byteBuffer.WriteBytes(data);

        byteBuffer.SetReaderIndex(startReadIndex + 32 + 65);
        ValueHash256 toSign = ValueKeccak.Compute(byteBuffer.ReadAllBytesAsSpan());
        byteBuffer.SetReaderIndex(startReadIndex);

        Signature signature = _ecdsa.Sign(_privateKey, in toSign);
        byteBuffer.SetWriterIndex(startWriteIndex + 32);
        byteBuffer.WriteBytes(signature.Bytes);
        byteBuffer.WriteByte(signature.RecoveryId);

        byteBuffer.SetReaderIndex(startReadIndex + 32);
        byteBuffer.SetWriterIndex(startWriteIndex + length);
        ValueHash256 mdc = ValueKeccak.Compute(byteBuffer.ReadAllBytesAsSpan());
        byteBuffer.SetReaderIndex(startReadIndex);

        byteBuffer.SetWriterIndex(startWriteIndex);
        byteBuffer.WriteBytes(mdc.BytesAsSpan);
        byteBuffer.SetWriterIndex(startWriteIndex + length);
    }

    protected void AddSignatureAndMdc(IByteBuffer byteBuffer, int dataLength)
    {
        // [<mdc 32 Bytes><sig 64 Bytes><SigRecoveryId><MsgType><Data>]
        int length = 32 + 64 + 1 + dataLength;

        int startReadIndex = byteBuffer.ReaderIndex;
        int startWriteIndex = byteBuffer.WriterIndex;

        byteBuffer.SetWriterIndex(startWriteIndex + length);
        byteBuffer.SetReaderIndex(startReadIndex + 32 + 65);
        ValueHash256 toSign = ValueKeccak.Compute(byteBuffer.ReadAllBytesAsSpan());
        byteBuffer.SetReaderIndex(startReadIndex);

        Signature signature = _ecdsa.Sign(_privateKey, in toSign);
        byteBuffer.SetWriterIndex(startWriteIndex + 32);
        byteBuffer.WriteBytes(signature.Bytes);
        byteBuffer.WriteByte(signature.RecoveryId);

        byteBuffer.SetWriterIndex(startWriteIndex + length);
        byteBuffer.SetReaderIndex(startReadIndex + 32);
        ValueHash256 mdc = ValueKeccak.Compute(byteBuffer.ReadAllBytesAsSpan());
        byteBuffer.SetReaderIndex(startReadIndex);

        byteBuffer.SetWriterIndex(startWriteIndex);
        byteBuffer.WriteBytes(mdc.BytesAsSpan);

        byteBuffer.SetReaderIndex(startReadIndex);
        byteBuffer.SetWriterIndex(startWriteIndex + length);
    }

    protected (PublicKey FarPublicKey, ValueHash256 Mdc, IByteBuffer Data) PrepareForDeserialization(IByteBuffer msg)
    {
        if (msg.ReadableBytes < 98)
        {
            throw new NetworkingException("Incorrect message", NetworkExceptionType.Validation);
        }
        IByteBuffer data = msg.Slice(98, msg.ReadableBytes - 98);
        Memory<byte> msgBytes = msg.ReadAllBytesAsMemory();
        ValueHash256 mdc = new(msgBytes.Span[..Hash256.Size]);
        Span<byte> sigAndData = msgBytes.Span[32..];
        Span<byte> computedMdc = ValueKeccak.Compute(sigAndData).BytesAsSpan;

        if (!Bytes.AreEqual(mdc.Bytes, computedMdc))
        {
            throw new NetworkingException("Invalid MDC", NetworkExceptionType.Validation);
        }

        PublicKey nodeId = _nodeIdResolver.GetNodeId(sigAndData[..64], sigAndData[64], sigAndData[65..]);
        return (nodeId, mdc, data);
    }

    protected static ValueHash256 ReadHash(IByteBuffer byteBuffer, int index)
    {
        Span<byte> hash = stackalloc byte[Hash256.Size];
        for (int i = 0; i < Hash256.Size; i++)
        {
            hash[i] = byteBuffer.GetByte(index + i);
        }

        return new ValueHash256(hash);
    }

    protected static void Encode<TWriter>(ref TWriter writer, IPEndPoint address, int length)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(length);
        IPAddressRlp.Encode(ref writer, address.Address);
        //tcp port
        writer.Encode(address.Port);
        //udp port
        writer.Encode(address.Port);
    }

    protected static int GetIPEndPointLength(IPEndPoint address)
    {
        int length = IPAddressRlp.GetLength(address.Address);
        length += Rlp.LengthOf(address.Port);
        length += Rlp.LengthOf(address.Port);
        return length;
    }

    protected static void SerializeNode<TWriter>(ref TWriter writer, IPEndPoint address, byte[] id)
        where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        int length = GetLengthSerializeNode(address, id);
        writer.StartSequence(length);
        IPAddressRlp.Encode(ref writer, address.Address);
        //tcp port
        writer.Encode(address.Port);
        //udp port
        writer.Encode(address.Port);
        writer.Encode(id);
    }

    protected static int GetLengthSerializeNode(IPEndPoint address, byte[] id)
    {
        int length = IPAddressRlp.GetLength(address.Address);
        length += Rlp.LengthOf(address.Port);
        length += Rlp.LengthOf(address.Port);
        length += Rlp.LengthOf(id);
        return length;
    }

    protected static void PrepareBufferForSerialization(IByteBuffer byteBuffer, int dataLength, byte msgType)
    {
        byteBuffer.EnsureWritable(MdcSigOffset + 1 + dataLength);
        byteBuffer.SetWriterIndex(byteBuffer.WriterIndex + MdcSigOffset);

        byteBuffer.WriteByte(msgType);
    }

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

        return new IPEndPoint(new IPAddress(ip), port);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidPort(int port) => throw new NetworkingException($"Invalid discovery port {port}.", NetworkExceptionType.Validation);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidIP(ReadOnlySpan<byte> ip) => throw new NetworkingException($"Invalid discovery IP {ip.ToHexString()}.", NetworkExceptionType.Validation);
    }
}
