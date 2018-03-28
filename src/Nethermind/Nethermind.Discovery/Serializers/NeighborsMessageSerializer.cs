using System.Collections.Generic;
using System.Linq;
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

        public NeighborsMessageSerializer(ISigner signer, PrivateKey privateKey, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver, INodeFactory nodeFactory) : base(signer, privateKey, messageFactory, nodeIdResolver, nodeFactory)
        {
        }

        public byte[] Serialize(NeighborsMessage message, IMessagePad pad = null)
        {
            byte[] typeBytes = { (byte)message.MessageType };

            byte[][] nodes = null;
            if (message.Nodes != null && message.Nodes.Any())
            {
                nodes = new byte[message.Nodes.Length][];
                for (var i = 0; i < message.Nodes.Length; i++)
                {
                    var node = message.Nodes[i];
                    var serializedNode = GetRlpAddressAndId(node.Address, node.Id.Bytes);
                    nodes[i] = serializedNode;
                }
            }

            var serializedNodes = nodes != null ? Rlp.Encode(nodes).Bytes : new[]{OffsetShortList};

            byte[] data = Rlp.Encode(
                serializedNodes,
                //TODO verify if encoding is correct
                Rlp.Encode(message.ExpirationTime)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeBytes, data, pad);
            return serializedMsg;
        }

        public NeighborsMessage Deserialize(byte[] msg)
        {
            var results = Deserialize<NeighborsMessage>(msg);

            Rlp rlp = new Rlp(results.Data);
            DecodedRlp decodedRaw = Rlp.Decode(rlp, RlpBehaviors.AllowExtraData);

            var serializedNodes = Rlp.Decode(new Rlp(decodedRaw.GetBytes(0)));

            var nodes = new List<Node>();
            if (serializedNodes != null && serializedNodes.Length > 0 && (serializedNodes.Length != 1 || serializedNodes.GetBytes(0)[1] != OffsetShortList))
            {
                for (var i = 0; i < serializedNodes.Length; i++)
                {
                    var serializedNode = serializedNodes.GetBytes(i);
                    DecodedRlp nodeRaw = Rlp.Decode(new Rlp(serializedNode));
                    var address = GetAddress(nodeRaw.GetBytes(0), nodeRaw.GetBytes(1));
                    //TODO confirm it is correct - based on EthereumJ
                    var idRaw = nodeRaw.Length > 3 ? nodeRaw.GetBytes(3) : nodeRaw.GetBytes(2);
                    byte[] id = idRaw;

                    var node = NodeFactory.CreateNode(new PublicKey(id), address);
                    nodes.Add(node);
                }
            }

            var expireTime = decodedRaw.GetBytes(1).ToInt64();
            var message = results.Message;
            message.Nodes = nodes.ToArray();
            message.ExpirationTime = expireTime;

            return message;
        }
    }
}