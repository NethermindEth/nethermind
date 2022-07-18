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

using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class PongMsgSerializer : DiscoveryMsgSerializerBase, IZeroMessageSerializer<PongMsg>
{
    public PongMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : base(ecdsa, nodeKey, nodeIdResolver)
    {
    }

    public void Serialize(IByteBuffer byteBuffer, PongMsg msg)
    {
        int length = GetLength(msg, out int contentLength);
        byteBuffer.EnsureWritable(length);

        RlpStream stream = new(length);

        if (msg.FarAddress is null)
        {
            throw new NetworkingException($"Sending discovery message without {nameof(msg.FarAddress)} set.",
                NetworkExceptionType.Discovery);
        }

        stream.StartSequence(contentLength);
        Encode(stream, msg.FarAddress);
        stream.Encode(msg.PingMdc);
        stream.Encode(msg.ExpirationTime);

        byte[] serializedMsg = Serialize((byte) msg.MsgType, stream.Data);
        byteBuffer.EnsureWritable(serializedMsg.Length);
        byteBuffer.WriteBytes(serializedMsg);
    }

    public PongMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, IByteBuffer Data) results = PrepareForDeserialization(msgBytes);

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

    private int GetLength(PongMsg message, out int contentLength)
    {
        if (message.FarAddress is null)
        {
            throw new NetworkingException($"Sending discovery message without {nameof(message.FarAddress)} set.",
                NetworkExceptionType.Discovery);
        }

        contentLength = Rlp.LengthOfSequence(GetIPEndPointLength(message.FarAddress));
        contentLength += Rlp.LengthOf(message.PingMdc);
        contentLength += Rlp.LengthOf(message.ExpirationTime);

        return Rlp.LengthOfSequence(contentLength);
    }
}
