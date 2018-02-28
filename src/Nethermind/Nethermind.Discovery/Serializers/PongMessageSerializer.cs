using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Network;

namespace Nethermind.Discovery.Serializers
{
    public class PongMessageSerializer : DiscoveryMessageSerializerBase, IMessageSerializer<PongMessage>
    {
        public PongMessageSerializer(ISigner signer, PrivateKey privateKey, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver, INodeFactory nodeFactory) : base(signer, privateKey, messageFactory, nodeIdResolver, nodeFactory)
        {
        }

        public byte[] Serialize(PongMessage message, IMessagePad pad = null)
        {
            byte[] typeBytes = { (byte)message.MessageType };
            byte[] sender = GetRlpAddress(message.FarAddress);
            byte[] token = Rlp.Encode(message.PingMdc).Bytes;
            byte[] data = Rlp.Encode(
                sender,
                token,
                Rlp.Encode(message.ExpirationTime)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeBytes, data, pad);
            return serializedMsg;
        }

        public PongMessage Deserialize(byte[] msg)
        {
            var results = Deserialize<PongMessage>(msg);

            var rlp = new Rlp(results.Data);
            var decodedRaw = (object[])Rlp.Decode(rlp, RlpBehaviors.AllowExtraData);

            var token = (byte[])Rlp.Decode(new Rlp((byte[])decodedRaw[1]));
            var expireTime = ((byte[])decodedRaw[2]).ToInt64();

            var message = results.Message;
            message.PingMdc = token;
            message.ExpirationTime = expireTime;

            return message;
        }
    }
}