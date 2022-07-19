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

public class EnrRequestMsgSerializer : DiscoveryMsgSerializerBase, IZeroMessageSerializer<EnrRequestMsg>
{
    public EnrRequestMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
        : base(ecdsa, nodeKey, nodeIdResolver) { }

    public void Serialize(IByteBuffer byteBuffer, EnrRequestMsg msg)
    {
        int length = GetLength(msg, out int contentLength);


        RlpStream stream = new(length);
        stream.StartSequence(contentLength);
        stream.Encode(msg.ExpirationTime);

        byte[] serializedMsg = Serialize((byte) msg.MsgType, stream.Data);
        byteBuffer.EnsureWritable(serializedMsg.Length);
        byteBuffer.WriteBytes(serializedMsg);
    }

    public EnrRequestMsg Deserialize(IByteBuffer msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, IByteBuffer Data) results = PrepareForDeserialization(msgBytes);
        NettyRlpStream rlpStream = new(results.Data);

        rlpStream.ReadSequenceLength();
        long expirationTime = rlpStream.DecodeLong();

        EnrRequestMsg msg = new (results.FarPublicKey, expirationTime);
        return msg;
    }

    private int GetLength(EnrRequestMsg message, out int contentLength)
    {
        contentLength = Rlp.LengthOf(message.ExpirationTime);
        return Rlp.LengthOfSequence(contentLength);
    }
}
