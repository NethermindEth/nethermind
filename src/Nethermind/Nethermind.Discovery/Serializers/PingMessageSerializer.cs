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
            var decodedRaw = (object[])Rlp.Decode(rlp, RlpBehaviors.AllowExtraData);
            var version = ((byte[])decodedRaw[0]).ToInt32();

            var sourceRaw = (object[])Rlp.Decode(new Rlp((byte[])decodedRaw[1]));
            var source = GetAddress((byte[])sourceRaw[0], (byte[])sourceRaw[1]);

            var destinationRaw = (object[])Rlp.Decode(new Rlp((byte[])decodedRaw[2]));
            var destination = GetAddress((byte[])destinationRaw[0], (byte[])destinationRaw[1]);

            var expireTime = ((byte[])decodedRaw[3]).ToInt64();

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