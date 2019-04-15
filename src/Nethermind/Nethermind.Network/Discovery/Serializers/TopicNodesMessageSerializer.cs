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
using Nethermind.Core.Model;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats;
using Node = Nethermind.Stats.Model.Node;

namespace Nethermind.Network.Discovery.Serializers
{
    public class TopicNodesMessageSerializer : DiscoveryMessageSerializerBase, IMessageSerializer<TopicNodesMessage>
    {
        public TopicNodesMessageSerializer(
            IEcdsa ecdsar,
            IPrivateKeyGenerator privateKeyGenerator,
            IDiscoveryMessageFactory messageFactory,
            INodeIdResolver nodeIdResolver) : base(ecdsar, privateKeyGenerator, messageFactory, nodeIdResolver)
        {
        }

        public byte[] Serialize(TopicNodesMessage message)
        {
            byte[] typeBytes = {(byte) message.MessageType};

            //TODO: turn this into a private, inline?, method
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
                Rlp.Encode(message.TopicQueryMdc),
                nodes == null ? Rlp.OfEmptySequence : Rlp.Encode(nodes)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeBytes, data);
            return serializedMsg;
        }

        public TopicNodesMessage Deserialize(byte[] msg)
        {
            var results = PrepareForDeserialization<TopicNodesMessage>(msg);

            var rlp = results.Data.AsRlpContext();
            rlp.ReadSequenceLength();
            var topicQueryMdc = rlp.DecodeByteArray();
            var nodes = DeserializeNodes(rlp);

            var message = results.Message;
            message.TopicQueryMdc = topicQueryMdc;
            message.Nodes = nodes;

            return message;
        }

        private Node[] DeserializeNodes(Rlp.DecoderContext context)
        {
            return context.DecodeArray(ctx =>
            {
                int lastPosition = ctx.ReadSequenceLength() + ctx.Position;
                int count = ctx.ReadNumberOfItemsRemaining(lastPosition);

                byte[] ip = ctx.DecodeByteArray();
                var address = GetAddress(ip, ctx.DecodeInt());
                if (count > 3)
                {
                    ctx.DecodeInt();
                }

                byte[] id = ctx.DecodeByteArray();
                return new Node(new PublicKey(id), address);
            });
        }
    }
}