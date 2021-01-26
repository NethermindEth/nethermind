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

using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Serializers
{
    public class FindNodeMessageSerializer : DiscoveryMessageSerializerBase, IMessageSerializer<FindNodeMessage>
    {
        public FindNodeMessageSerializer(IEcdsa ecdsa, IPrivateKeyGenerator privateKeyGenerator, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver) : base(ecdsa, privateKeyGenerator, messageFactory, nodeIdResolver)
        {
        }

        public byte[] Serialize(FindNodeMessage message)
        {
            byte[] data = Rlp.Encode(
                Rlp.Encode(message.SearchedNodeId),
                //verify if encoding is correct
                Rlp.Encode(message.ExpirationTime)
            ).Bytes;

            byte[] serializedMsg = Serialize((byte) message.MessageType, data);
            return serializedMsg;
        }

        public FindNodeMessage Deserialize(byte[] msg)
        {
            (FindNodeMessage Message, byte[] Mdc, byte[] Data) results = PrepareForDeserialization<FindNodeMessage>(msg);
            RlpStream rlpStream = results.Data.AsRlpStream();

            rlpStream.ReadSequenceLength();
            byte[] searchedNodeId = rlpStream.DecodeByteArray();
            long expirationTime = rlpStream.DecodeLong();

            FindNodeMessage message = results.Message;
            message.SearchedNodeId = searchedNodeId;
            message.ExpirationTime = expirationTime;

            return message;
        }
    }
}
