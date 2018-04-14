using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Network;

namespace Nethermind.Discovery.Serializers
{
    public class DiscoveryMsgSerializersProvider : IDiscoveryMsgSerializersProvider
    {
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly PingMessageSerializer _pingMessageSerializer;
        private readonly PongMessageSerializer _pongMessageSerializer;
        private readonly FindNodeMessageSerializer _findNodeMessageSerializer;
        private readonly NeighborsMessageSerializer _neighborsMessageSerializer;

        public DiscoveryMsgSerializersProvider(IMessageSerializationService messageSerializationService, ISigner signer, IPrivateKeyProvider privateKeyProvider, IDiscoveryMessageFactory messageFactory, INodeIdResolver nodeIdResolver, INodeFactory nodeFactory)
        {
            var pingSerializer = new PingMessageSerializer(signer, privateKeyProvider, messageFactory, nodeIdResolver, nodeFactory);
            var pongSerializer = new PongMessageSerializer(signer, privateKeyProvider, messageFactory, nodeIdResolver, nodeFactory);
            var findNodeSerializer = new FindNodeMessageSerializer(signer, privateKeyProvider, messageFactory, nodeIdResolver, nodeFactory);
            var neighborsSerializer = new NeighborsMessageSerializer(signer, privateKeyProvider, messageFactory, nodeIdResolver, nodeFactory);

            _messageSerializationService = messageSerializationService;
            _pingMessageSerializer = pingSerializer;
            _pongMessageSerializer = pongSerializer;
            _findNodeMessageSerializer = findNodeSerializer;
            _neighborsMessageSerializer = neighborsSerializer;
        }

        public void RegisterDiscoverySerializers()
        {
            _messageSerializationService.Register(_pingMessageSerializer);
            _messageSerializationService.Register(_pongMessageSerializer);
            _messageSerializationService.Register(_findNodeMessageSerializer);
            _messageSerializationService.Register(_neighborsMessageSerializer);
        }
    }
}