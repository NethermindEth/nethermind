/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Network;

namespace Nethermind.Discovery.Serializers
{
    public class FindNodeMessageSerializer : DiscoveryMessageSerializerBase, IMessageSerializer<FindNodeMessage>
    {
        public FindNodeMessageSerializer(ISigner signer, IPrivateKeyProvider privateKeyProvider, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver, INodeFactory nodeFactory) : base(signer, privateKeyProvider, messageFactory, nodeIdResolver, nodeFactory)
        {
        }

        public byte[] Serialize(FindNodeMessage message)
        {
            byte[] typeBytes = { (byte)message.MessageType };
            byte[] data = Rlp.Encode(
                Rlp.Encode(message.SearchedNodeId),
                //verify if encoding is correct
                Rlp.Encode(message.ExpirationTime)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeBytes, data);
            return serializedMsg;
        }

        public FindNodeMessage Deserialize(byte[] msg)
        {
            var results = PrepareForDeserialization<FindNodeMessage>(msg);
            Rlp.DecoderContext context = results.Data.AsRlpContext();

            context.ReadSequenceLength();
            var searchedNodeId = context.DecodeByteArray();
            var expireTime = context.DecodeLong();

            var message = results.Message;
            message.SearchedNodeId = searchedNodeId;
            message.ExpirationTime = expireTime;

            return message;
        }
    }
}