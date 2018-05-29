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

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Network;
using Node = Nethermind.Discovery.RoutingTable.Node;

namespace Nethermind.Discovery.Serializers
{
    public class NeighborsMessageSerializer : DiscoveryMessageSerializerBase, IMessageSerializer<NeighborsMessage>
    {
        public NeighborsMessageSerializer(ISigner signer, IPrivateKeyProvider privateKeyProvider, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver, INodeFactory nodeFactory) : base(signer, privateKeyProvider, messageFactory, nodeIdResolver, nodeFactory)
        {
        }

        public byte[] Serialize(NeighborsMessage message)
        {
            byte[] typeBytes = { (byte)message.MessageType };

            Rlp[] nodes = null;
            if (message.Nodes != null && message.Nodes.Any())
            {
                nodes = new Rlp[message.Nodes.Length];
                for (var i = 0; i < message.Nodes.Length; i++)
                {
                    var node = message.Nodes[i];
                    var serializedNode = SerializeNode(node.Address, node.Id.Bytes);
                    nodes[i] = serializedNode;
                }
            }
            
            byte[] data = Rlp.Encode(
                nodes == null ? Rlp.OfEmptySequence : Rlp.Encode(nodes),
                //TODO verify if encoding is correct
                Rlp.Encode(message.ExpirationTime)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeBytes, data);
            return serializedMsg;
        }

        public NeighborsMessage Deserialize(byte[] msg)
        {
            var results = PrepareForDeserialization<NeighborsMessage>(msg);

            var rlp = results.Data.AsRlpContext();
            rlp.ReadSequenceLength();
            var nodes = DeserializeNodes(rlp);

            var expirationTime = rlp.DecodeLong();
            var message = results.Message;
            message.Nodes = nodes;
            message.ExpirationTime = expirationTime;

            return message;
        }

        private Node[] DeserializeNodes(Rlp.DecoderContext context)
        {
            return context.DecodeArray(ctx =>
            {
                int lastPosition = ctx.ReadSequenceLength() + ctx.Position;
                int count = ctx.ReadNumberOfItemsRemaining(lastPosition);
                var address = GetAddress(ctx.DecodeByteArray(), ctx.DecodeInt());
                if (count > 3)
                {
                    ctx.DecodeInt();
                }

                byte[] id = ctx.DecodeByteArray();
                return NodeFactory.CreateNode(new PublicKey(id), address);
            });
        }
    }
}