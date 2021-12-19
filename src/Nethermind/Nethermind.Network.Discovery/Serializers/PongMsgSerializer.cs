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

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers;

public class PongMsgSerializer : DiscoveryMsgSerializerBase, IMessageSerializer<PongMsg>
{
    public PongMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator privateKeyGenerator, INodeIdResolver nodeIdResolver) : base(ecdsa, privateKeyGenerator, nodeIdResolver)
    {
    }

    public byte[] Serialize(PongMsg msg)
    {
        if (msg.FarAddress is null)
        {
            throw new NetworkingException($"Sending discovery message without {nameof(msg.FarAddress)} set.",
                NetworkExceptionType.Discovery);
        }
        
        byte[] data = Rlp.Encode(
            Encode(msg.FarAddress),
            Rlp.Encode(msg.PingMdc),
            Rlp.Encode(msg.ExpirationTime)
        ).Bytes;

        byte[] serializedMsg = Serialize((byte) msg.MsgType, data);
        return serializedMsg;
    }

    public PongMsg Deserialize(byte[] msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, byte[] Data) results = PrepareForDeserialization<PongMsg>(msgBytes);

        RlpStream rlp = results.Data.AsRlpStream();

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
}
