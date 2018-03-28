using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Network;

namespace Nethermind.Discovery.Serializers
{
    public class PingMessageSerializer : DiscoveryMessageSerializerBase, IMessageSerializer<PingMessage>
    {
        public PingMessageSerializer(ISigner signer, PrivateKey privateKey, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver, INodeFactory nodeFactory) : base(signer, privateKey, messageFactory, nodeIdResolver, nodeFactory)
        {
        }

        public byte[] Serialize(PingMessage message, IMessagePad pad = null)
        {
            byte[] typeBytes = { (byte)message.MessageType };
            byte[] source = GetRlpAddress(message.SourceAddress);
            byte[] destination = GetRlpAddress(message.DestinationAddress);
            byte[] data = Rlp.Encode(
                Rlp.Encode(message.Version),
                source,
                destination,
                //verify if encoding is correct
                Rlp.Encode(message.ExpirationTime)
            ).Bytes;

            byte[] serializedMsg = Serialize(typeBytes, data, pad);
            return serializedMsg;
        }

        public PingMessage Deserialize(byte[] msg)
        {
            var results = Deserialize<PingMessage>(msg);
            
            var rlp = new Rlp(results.Data);
            DecodedRlp decodedRaw = Rlp.Decode(rlp, RlpBehaviors.AllowExtraData);
            var version = decodedRaw.GetInt(0);

            var sourceRaw = Rlp.Decode(new Rlp(decodedRaw.GetBytes(1)));
            var source = GetAddress(sourceRaw.GetBytes(0), sourceRaw.GetBytes(1));

            var destinationRaw = Rlp.Decode(new Rlp(decodedRaw.GetBytes(2)));
            var destination = GetAddress(destinationRaw.GetBytes(0), destinationRaw.GetBytes(1));

            var expireTime = (decodedRaw.GetBytes(3)).ToInt64();

            var message = results.Message;
            message.SourceAddress = source;
            message.DestinationAddress = destination;
            message.Mdc = results.Mdc;
            message.Version = version;
            message.ExpirationTime = expireTime;

            return message;
        }
    }
}