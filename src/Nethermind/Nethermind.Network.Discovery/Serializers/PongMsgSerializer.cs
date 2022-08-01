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

using System.Buffers;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class PongMsgSerializer : DiscoveryMsgSerializerBase, IZeroInnerMessageSerializer<PongMsg>
{
    public PongMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : base(ecdsa, nodeKey, nodeIdResolver)
    {
    }

    public void Serialize(IByteBuffer byteBuffer, PongMsg msg)
    {
        if (msg.FarAddress is null)
        {
            throw new NetworkingException($"Sending discovery message without {nameof(msg.FarAddress)} set.", NetworkExceptionType.Discovery);
        }

        (int totalLength, int contentLength, int farAddressLength) = GetLength(msg);
        byteBuffer.EnsureWritable(totalLength);

        byte[] array = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            RlpStream stream = new(array);
            stream.StartSequence(contentLength);
            Encode(stream, msg.FarAddress, farAddressLength);
            stream.Encode(msg.PingMdc);
            stream.Encode(msg.ExpirationTime);

            Serialize((byte)msg.MsgType, stream.Data.AsSpan(0, totalLength), byteBuffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    public PongMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey FarPublicKey, Memory<byte> Mdc, IByteBuffer Data) results = PrepareForDeserialization(msgBytes);

        NettyRlpStream rlp = new(results.Data);

        rlp.ReadSequenceLength();
        rlp.ReadSequenceLength();

        // GetAddress(rlp.DecodeByteArray(), rlp.DecodeInt());
        rlp.DecodeByteArraySpan();
        rlp.DecodeInt(); // UDP port (we ignore and take it from Netty)
        rlp.DecodeInt(); // TCP port
        byte[] token = rlp.DecodeByteArray();
        long expirationTime = rlp.DecodeLong();

        PongMsg msg = new(results.FarPublicKey, expirationTime, token);
        return msg;
    }

    public int GetLength(PongMsg message, out int contentLength)
    {
        (int totalLength, contentLength, int _) = GetLength(message);
        return totalLength;
    }

    private (int totalLength, int contentLength, int farAddressLength) GetLength(PongMsg message)
    {
        if (message.FarAddress is null)
        {
            throw new NetworkingException($"Sending discovery message without {nameof(message.FarAddress)} set.",
                NetworkExceptionType.Discovery);
        }

        int farAddressLength = GetIPEndPointLength(message.FarAddress);
        int contentLength = Rlp.LengthOfSequence(farAddressLength);
        contentLength += Rlp.LengthOf(message.PingMdc);
        contentLength += Rlp.LengthOf(message.ExpirationTime);

        return (Rlp.LengthOfSequence(contentLength), contentLength, farAddressLength);
    }
}
