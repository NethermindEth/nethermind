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

using System.Collections.Generic;
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
        //TODO config if we have it anywhere else
        //private static final int OFFSET_SHORT_LIST = 0xc0; from EthereumJ
        private static readonly byte OffsetShortList = 0xc0;

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
                    var serializedNode = GetRlpAddressAndId(node.Address, node.Id.Bytes);
                    nodes[i] = serializedNode;
                }
            }

            byte[] data = Rlp.Encode(
                nodes ?? (object)new[] { OffsetShortList },
                //TODO verify if encoding is correct
                Rlp.Encode(message.ExpirationTime)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeBytes, data);
            return serializedMsg;
        }

        public NeighborsMessage Deserialize(byte[] msg)
        {
            var results = Deserialize<NeighborsMessage>(msg);

            var rlp = new Rlp(results.Data);
            var decodedRaw = Rlp.Decode(rlp, RlpBehaviors.AllowExtraData);

            var nodes = DeserializeNodes(decodedRaw);

            var expireTime = decodedRaw.GetBytes(1).ToInt64();
            var message = results.Message;
            message.Nodes = nodes.ToArray();
            message.ExpirationTime = expireTime;

            return message;
        }

        private List<Node> DeserializeNodes(DecodedRlp decodedRaw)
        {
            if (!(decodedRaw.Items[0] is DecodedRlp))
            {
                return new List<Node>();
            }

            var decodedRlp = (DecodedRlp)decodedRaw.Items[0];
            var decodedNodes = decodedRlp != null
                ? (decodedRlp.IsSequence ? decodedRlp.Items : new List<object> { decodedRlp.SingleItem })
                : new List<object>();

            var nodes = new List<Node>();
            for (var i = 0; i < decodedNodes.Count; i++)
            {
                DecodedRlp nodeRaw = (DecodedRlp) decodedNodes[i];
                if (i == 0 && !nodeRaw.IsSequence && ((byte[]) nodeRaw.SingleItem)[0] == OffsetShortList)
                {
                    break;
                }

                var address = GetAddress(nodeRaw.GetBytes(0), nodeRaw.GetBytes(1));
                //TODO confirm it is correct - based on EthereumJ
                var idRaw = nodeRaw.Length > 3 ? nodeRaw.GetBytes(3) : nodeRaw.GetBytes(2);
                byte[] id = idRaw;

                var node = NodeFactory.CreateNode(new PublicKey(id), address);
                nodes.Add(node);
            }

            return nodes;
        }
    }
}