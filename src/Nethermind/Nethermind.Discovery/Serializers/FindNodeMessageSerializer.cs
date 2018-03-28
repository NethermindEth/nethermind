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
        public FindNodeMessageSerializer(ISigner signer, PrivateKey privateKey, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver, INodeFactory nodeFactory) : base(signer, privateKey, messageFactory, nodeIdResolver, nodeFactory)
        {
        }

        public byte[] Serialize(FindNodeMessage message, IMessagePad pad = null)
        {
            byte[] typeBytes = { (byte)message.MessageType };
            byte[] searchedNodeId = Rlp.Encode(message.SearchedNodeId).Bytes;
            byte[] data = Rlp.Encode(
                searchedNodeId,
                //verify if encoding is correct
                Rlp.Encode(message.ExpirationTime)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeBytes, data, pad);
            return serializedMsg;
        }

        public FindNodeMessage Deserialize(byte[] msg)
        {
            var results = Deserialize<FindNodeMessage>(msg);

            var rlp = new Rlp(results.Data);
            DecodedRlp raw = Rlp.Decode(rlp, RlpBehaviors.AllowExtraData);

            var searchedNodeId = Rlp.Decode<byte[]>(new Rlp(raw.GetBytes(0)));
            var expireTime = raw.GetBytes(1).ToInt64();

            var message = results.Message;
            message.SearchedNodeId = searchedNodeId;
            message.ExpirationTime = expireTime;

            return message;
        }
    }
}