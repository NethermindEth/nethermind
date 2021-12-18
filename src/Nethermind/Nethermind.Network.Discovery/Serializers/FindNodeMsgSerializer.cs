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

public class FindNodeMsgSerializer : DiscoveryMsgSerializerBase, IMessageSerializer<FindNodeMsg>
{
    public FindNodeMsgSerializer(IEcdsa ecdsa, IPrivateKeyGenerator privateKeyGenerator, INodeIdResolver nodeIdResolver)
        : base(ecdsa, privateKeyGenerator, nodeIdResolver) { }

    public byte[] Serialize(FindNodeMsg msg)
    {
        byte[] data = Rlp.Encode(
            Rlp.Encode(msg.SearchedNodeId),
            //verify if encoding is correct
            Rlp.Encode(msg.ExpirationTime)
        ).Bytes;

        byte[] serializedMsg = Serialize((byte) msg.MsgType, data);
        return serializedMsg;
    }

    public FindNodeMsg Deserialize(byte[] msgBytes)
    {
        (PublicKey FarPublicKey, byte[] Mdc, byte[] Data) results = PrepareForDeserialization<FindNodeMsg>(msgBytes);
        RlpStream rlpStream = results.Data.AsRlpStream();

        rlpStream.ReadSequenceLength();
        byte[] searchedNodeId = rlpStream.DecodeByteArray();
        long expirationTime = rlpStream.DecodeLong();

        FindNodeMsg findNodeMsg = new (results.FarPublicKey, expirationTime, searchedNodeId);
        return findNodeMsg;
    }
}
