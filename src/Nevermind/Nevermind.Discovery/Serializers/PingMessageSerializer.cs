using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Extensions;
using Nevermind.Discovery.Messages;
using Nevermind.Discovery.RoutingTable;
using Nevermind.Network;

namespace Nevermind.Discovery.Serializers
{
    public class PingMessageSerializer : DiscoveryMessageSerializerBase, IMessageSerializer<PingMessage>
    {
        public PingMessageSerializer(ISigner signer, PrivateKey privateKey, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver, INodeFactory nodeFactory) : base(signer, privateKey, messageFactory, nodeIdResolver, nodeFactory)
        {
        }

        public byte[] Serialize(PingMessage message, IMessagePad pad = null)
        {
            byte[] typeBytes = { (byte)message.MessageType };
            byte[] sender = GetRlpAddress(message.SourceAddress);
            byte[] receiver = GetRlpAddress(message.DestinationAddress);
            byte[] data = Rlp.Encode(
                Rlp.Encode(message.Version),
                Rlp.Encode(sender),
                Rlp.Encode(receiver),
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
            var decodedRaw = (object[])Rlp.Decode(rlp, RlpBehaviors.AllowExtraData);
            var version = ((byte[])Rlp.Decode(new Rlp((byte[])decodedRaw[0]))).ToInt32();

            var sourceRaw = (object[])Rlp.Decode(new Rlp((byte[])decodedRaw[1]));
            var source = GetAddress((byte[])sourceRaw[0], (byte[])sourceRaw[1]);

            var destinationRaw = (object[])Rlp.Decode(new Rlp((byte[])decodedRaw[2]));
            var destination = GetAddress((byte[])destinationRaw[0], (byte[])destinationRaw[1]);

            var expireTime = ((byte[])Rlp.Decode(new Rlp((byte[])decodedRaw[3]))).ToInt64();

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