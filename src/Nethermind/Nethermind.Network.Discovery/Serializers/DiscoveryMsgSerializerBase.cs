// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public abstract class DiscoveryMsgSerializerBase
{
    private readonly PrivateKey _privateKey;
    protected readonly IEcdsa _ecdsa;

    private readonly INodeIdResolver _nodeIdResolver;

    protected const int MdcSigOffset = 32 + 64 + 1;

    protected DiscoveryMsgSerializerBase(IEcdsa ecdsa,
        IPrivateKeyGenerator nodeKey,
        INodeIdResolver nodeIdResolver)
    {
        _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
        _privateKey = nodeKey.Generate();
        _nodeIdResolver = nodeIdResolver ?? throw new ArgumentNullException(nameof(nodeIdResolver));
    }

    protected void Serialize(byte type, Span<byte> data, IByteBuffer byteBuffer)
    {
        // [<mdc 32 Bytes><sig 64 Bytes><SigRecoveryId><MsgType><Data>]
        int length = 32 + 1 + data.Length + 64 + 1;
        byteBuffer.EnsureWritable(length);

        int startReadIndex = byteBuffer.ReaderIndex;
        int startWriteIndex = byteBuffer.WriterIndex;

        byteBuffer.SetWriterIndex(startWriteIndex + 32 + 65);
        byteBuffer.WriteByte(type);
        byteBuffer.WriteBytes(data.ToArray(), 0, data.Length);

        byteBuffer.SetReaderIndex(startReadIndex + 32 + 65);
        Keccak toSign = Keccak.Compute(byteBuffer.ReadAllBytesAsSpan());
        byteBuffer.SetReaderIndex(startReadIndex);

        Signature signature = _ecdsa.Sign(_privateKey, toSign);
        byteBuffer.SetWriterIndex(startWriteIndex + 32);
        byteBuffer.WriteBytes(signature.Bytes, 0, 64);
        byteBuffer.WriteByte(signature.RecoveryId);

        byteBuffer.SetReaderIndex(startReadIndex + 32);
        byteBuffer.SetWriterIndex(startWriteIndex + length);
        ValueKeccak mdc = ValueKeccak.Compute(byteBuffer.ReadAllBytesAsSpan());
        byteBuffer.SetReaderIndex(startReadIndex);

        byteBuffer.SetWriterIndex(startWriteIndex);
        byteBuffer.WriteBytes(mdc.BytesAsSpan.ToArray(), 0, 32);
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
        Keccak toSign = Keccak.Compute(byteBuffer.ReadAllBytesAsSpan());
        byteBuffer.SetReaderIndex(startReadIndex);

        Signature signature = _ecdsa.Sign(_privateKey, toSign);
        byteBuffer.SetWriterIndex(startWriteIndex + 32);
        byteBuffer.WriteBytes(signature.Bytes, 0, 64);
        byteBuffer.WriteByte(signature.RecoveryId);

        byteBuffer.SetWriterIndex(startWriteIndex + length);
        byteBuffer.SetReaderIndex(startReadIndex + 32);
        ValueKeccak mdc = ValueKeccak.Compute(byteBuffer.ReadAllBytesAsSpan());
        byteBuffer.SetReaderIndex(startReadIndex);

        byteBuffer.SetWriterIndex(startWriteIndex);
        byteBuffer.WriteBytes(mdc.BytesAsSpan.ToArray(), 0, 32);

        byteBuffer.SetReaderIndex(startReadIndex);
        byteBuffer.SetWriterIndex(startWriteIndex + length);
    }

    protected (PublicKey FarPublicKey, Memory<byte> Mdc, IByteBuffer Data) PrepareForDeserialization(IByteBuffer msg)
    {
        if (msg.ReadableBytes < 98)
        {
            throw new NetworkingException("Incorrect message", NetworkExceptionType.Validation);
        }
        IByteBuffer data = msg.Slice(98, msg.ReadableBytes - 98);
        Memory<byte> msgBytes = msg.ReadAllBytesAsMemory();
        Memory<byte> mdc = msgBytes[..32];
        Span<byte> sigAndData = msgBytes.Span[32..];
        Span<byte> computedMdc = ValueKeccak.Compute(sigAndData).BytesAsSpan;

        if (!Bytes.AreEqual(mdc.Span, computedMdc))
        {
            throw new NetworkingException("Invalid MDC", NetworkExceptionType.Validation);
        }

        PublicKey nodeId = _nodeIdResolver.GetNodeId(sigAndData[..64], sigAndData[64], sigAndData[65..]);
        return (nodeId, mdc, data);
    }

    protected static void Encode(RlpStream stream, IPEndPoint address, int length)
    {
        stream.StartSequence(length);
        stream.Encode(address.Address.GetAddressBytes());
        //tcp port
        stream.Encode(address.Port);
        //udp port
        stream.Encode(address.Port);
    }

    protected static int GetIPEndPointLength(IPEndPoint address)
    {
        int length = Rlp.LengthOf(address.Address.GetAddressBytes());
        length += Rlp.LengthOf(address.Port);
        length += Rlp.LengthOf(address.Port);
        return length;
    }

    protected static void SerializeNode(RlpStream stream, IPEndPoint address, byte[] id)
    {
        int length = GetLengthSerializeNode(address, id);
        stream.StartSequence(length);
        stream.Encode(address.Address.GetAddressBytes());
        //tcp port
        stream.Encode(address.Port);
        //udp port
        stream.Encode(address.Port);
        stream.Encode(id);
    }

    protected static int GetLengthSerializeNode(IPEndPoint address, byte[] id)
    {
        int length = Rlp.LengthOf(address.Address.GetAddressBytes());
        length += Rlp.LengthOf(address.Port);
        length += Rlp.LengthOf(address.Port);
        length += Rlp.LengthOf(id);
        return length;
    }

    protected void PrepareBufferForSerialization(IByteBuffer byteBuffer, int dataLength, byte msgType)
    {
        byteBuffer.EnsureWritable(MdcSigOffset + 1 + dataLength);
        byteBuffer.SetWriterIndex(byteBuffer.WriterIndex + MdcSigOffset);

        byteBuffer.WriteByte(msgType);
    }

    protected static IPEndPoint GetAddress(ReadOnlySpan<byte> ip, int port)
    {
        IPAddress ipAddress;
        try
        {
            ipAddress = new IPAddress(ip);
        }
        catch (Exception)
        {
            ipAddress = IPAddress.Any;
        }

        return new IPEndPoint(ipAddress, port);
    }
}
