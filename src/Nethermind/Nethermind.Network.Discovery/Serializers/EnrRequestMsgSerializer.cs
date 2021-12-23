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

public class EnrRequestMsgSerializer : DiscoveryMsgSerializerBase, IMessageSerializer<EnrRequestMsg>
{
    public EnrRequestMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver)
        : base(ecdsa, nodeKey, nodeIdResolver) { }

    public byte[] Serialize(EnrRequestMsg msg)
    {
        // TODO: optimize
        byte[] data = Rlp.Encode(
            Rlp.Encode(msg.ExpirationTime)
        ).Bytes;

        byte[] serializedMsg = Serialize((byte) msg.MsgType, data);
        return serializedMsg;
    }

    public EnrRequestMsg Deserialize(byte[] msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, byte[] Data) results = PrepareForDeserialization(msgBytes);
        RlpStream rlpStream = results.Data.AsRlpStream();

        rlpStream.ReadSequenceLength();
        long expirationTime = rlpStream.DecodeLong();

        EnrRequestMsg msg = new (results.FarPublicKey, expirationTime);
        return msg;
    }
}
