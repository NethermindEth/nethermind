using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;
using Nevermind.Discovery.Messages;
using Nevermind.Discovery.RoutingTable;
using Nevermind.Network;

namespace Nevermind.Discovery.Serializers
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
            var decodedRaw = (object[])Rlp.Decode(rlp, RlpBehaviors.AllowExtraData);

            var searchedNodeId = (byte[])Rlp.Decode(new Rlp((byte[])decodedRaw[0]));
            var expireTime = ((byte[])decodedRaw[1]).ToInt64();

            var message = results.Message;
            message.SearchedNodeId = searchedNodeId;
            message.ExpirationTime = expireTime;

            return message;
        }
    }
}