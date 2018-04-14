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
                message.SearchedNodeId,
                //verify if encoding is correct
                Rlp.Encode(message.ExpirationTime)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeBytes, data);
            return serializedMsg;
        }

        public FindNodeMessage Deserialize(byte[] msg)
        {
            var results = Deserialize<FindNodeMessage>(msg);

            var rlp = new Rlp(results.Data);
            DecodedRlp raw = Rlp.Decode(rlp, RlpBehaviors.AllowExtraData);

            var searchedNodeId = raw.GetBytes(0);
            var expireTime = raw.GetBytes(1).ToInt64();

            var message = results.Message;
            message.SearchedNodeId = searchedNodeId;
            message.ExpirationTime = expireTime;

            return message;
        }
    }
}