//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
        int length = 32 + 1 + data.Length + 64 + 1;
        byteBuffer.EnsureWritable(length);
        Span<byte> resultSpan = stackalloc byte[length];
        resultSpan[32 + 65] = type;
        data.CopyTo(resultSpan.Slice(32 + 65 + 1, data.Length));

        Span<byte> payload = resultSpan.Slice(32 + 65);
        Keccak toSign = Keccak.Compute(payload);
        Signature signature = _ecdsa.Sign(_privateKey, toSign);
        signature.Bytes.AsSpan().CopyTo(resultSpan.Slice(32, 64));
        resultSpan[32 + 64] = signature.RecoveryId;

        Span<byte> forMdc = resultSpan.Slice(32);
        ValueKeccak mdc = ValueKeccak.Compute(forMdc);
        mdc.BytesAsSpan.CopyTo(resultSpan.Slice(0,32));
        byteBuffer.EnsureWritable(resultSpan.Length);
        byteBuffer.WriteBytes(resultSpan);
    }

    protected (PublicKey FarPublicKey, Memory<byte> Mdc, IByteBuffer Data) PrepareForDeserialization(IByteBuffer msg)
    {
        if (msg.ReadableBytes < 98)
        {
            throw new NetworkingException("Incorrect message", NetworkExceptionType.Validation);
        }
        IByteBuffer data = msg.Slice(98, msg.ReadableBytes - 98);
        Memory<byte> msgBytes = msg.ReadAllBytesAsMemory();
        Memory<byte> mdc = msgBytes.Slice(0, 32);
        Span<byte> sigAndData = msgBytes.Span.Slice(32);
        Span<byte> computedMdc = ValueKeccak.Compute(sigAndData).BytesAsSpan;

        if (!Bytes.AreEqual(mdc.Span, computedMdc))
        {
            throw new NetworkingException("Invalid MDC", NetworkExceptionType.Validation);
        }

        PublicKey nodeId = _nodeIdResolver.GetNodeId(sigAndData.Slice(0, 64), sigAndData[64], sigAndData.Slice(65, sigAndData.Length - 65));
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
