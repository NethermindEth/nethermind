﻿/*
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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Stats;

namespace Nethermind.Network.Discovery.Serializers
{
    public class DiscoveryMsgSerializersProvider : IDiscoveryMsgSerializersProvider
    {
        private readonly IMessageSerializationService _messageSerializationService;
        private readonly PingMessageSerializer _pingMessageSerializer;
        private readonly PongMessageSerializer _pongMessageSerializer;
        private readonly FindNodeMessageSerializer _findNodeMessageSerializer;
        private readonly FindNodeHashMessageSerializer _findNodeHashMessageSerializer;
        private readonly NeighborsMessageSerializer _neighborsMessageSerializer;
        private readonly TopicRegisterMessageSerializer _topicRegisterMessageSerializer;
        private readonly TopicQueryMessageSerializer _topicQueryMessageSerializer;
        private readonly TopicNodesMessageSerializer _topicNodesMessageSerializer;

        public DiscoveryMsgSerializersProvider(
            IMessageSerializationService messageSerializationService,
            IEcdsa ecdsa,
            IPrivateKeyGenerator privateKeyGenerator,
            IDiscoveryMessageFactory messageFactory,
            INodeIdResolver nodeIdResolver)
        {
            var pingSerializer = new PingMessageSerializer(ecdsa, privateKeyGenerator, messageFactory, nodeIdResolver);
            var pongSerializer = new PongMessageSerializer(ecdsa, privateKeyGenerator, messageFactory, nodeIdResolver);
            var findNodeSerializer = new FindNodeMessageSerializer(ecdsa, privateKeyGenerator, messageFactory, nodeIdResolver);
            var findNodeHashSerializer = new FindNodeHashMessageSerializer(ecdsa, privateKeyGenerator, messageFactory, nodeIdResolver);
            var neighborsSerializer = new NeighborsMessageSerializer(ecdsa, privateKeyGenerator, messageFactory, nodeIdResolver);
            var topicRegisterSerializer = new TopicRegisterMessageSerializer(ecdsa, privateKeyGenerator, messageFactory, nodeIdResolver);
            var topicQuerySerializer = new TopicQueryMessageSerializer(ecdsa, privateKeyGenerator, messageFactory, nodeIdResolver);
            var topicNodesSerializer = new TopicNodesMessageSerializer(ecdsa, privateKeyGenerator, messageFactory, nodeIdResolver);

            _messageSerializationService = messageSerializationService;
            _pingMessageSerializer = pingSerializer;
            _pongMessageSerializer = pongSerializer;
            _findNodeMessageSerializer = findNodeSerializer;
            _findNodeHashMessageSerializer = findNodeHashSerializer;
            _neighborsMessageSerializer = neighborsSerializer;
            _topicRegisterMessageSerializer = topicRegisterSerializer;
            _topicQueryMessageSerializer = topicQuerySerializer;
            _topicNodesMessageSerializer = topicNodesSerializer;
        }

        public void RegisterDiscoverySerializers()
        {
            _messageSerializationService.Register(_pingMessageSerializer);
            _messageSerializationService.Register(_pongMessageSerializer);
            _messageSerializationService.Register(_findNodeMessageSerializer);
            _messageSerializationService.Register(_findNodeHashMessageSerializer);
            _messageSerializationService.Register(_neighborsMessageSerializer);
            _messageSerializationService.Register(_topicRegisterMessageSerializer);
            _messageSerializationService.Register(_topicQueryMessageSerializer);
            _messageSerializationService.Register(_topicNodesMessageSerializer);
        }
    }
}