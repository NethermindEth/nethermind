/*
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

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Stats;

namespace Nethermind.Network.Test.Builders
{
    public class SerializationBuilder : BuilderBase<IMessageSerializationService>
    {
        private readonly ITimestamp _timestamp;

        public SerializationBuilder(ITimestamp timestamp = null)
        {
            _timestamp = timestamp ?? new Timestamp();
            TestObject = new MessageSerializationService();
        }

        public SerializationBuilder With<T>(IMessageSerializer<T> serializer) where T : MessageBase
        {
            TestObject.Register(serializer);
            return this;
        }

        public SerializationBuilder WithEncryptionHandshake()
        {
            return With(new AuthMessageSerializer())
                .With(new AuthEip8MessageSerializer(new Eip8MessagePad(new CryptoRandom())))
                .With(new AckMessageSerializer())
                .With(new AckEip8MessageSerializer(new Eip8MessagePad(new CryptoRandom())));
        }
            
        public SerializationBuilder WithP2P()
        {
            return With(new Network.P2P.PingMessageSerializer())
                .With(new Network.P2P.PongMessageSerializer())
                .With(new Network.P2P.HelloMessageSerializer())
                .With(new Network.P2P.DisconnectMessageSerializer());
        }

        public SerializationBuilder WithEth()
        {
            return With(new BlockHeadersMessageSerializer())
                .With(new BlockBodiesMessageSerializer())
                .With(new GetBlockBodiesMessageSerializer())
                .With(new GetBlockHeadersMessageSerializer())
                .With(new NewBlockHashesMessageSerializer())
                .With(new NewBlockMessageSerializer())
                .With(new TransactionsMessageSerializer())
                .With(new StatusMessageSerializer());
        }

        public SerializationBuilder WithDiscovery(PrivateKey privateKey)
        {
            INetworkConfig networkConfig = new NetworkConfig();
            Ecdsa ecdsa = new Ecdsa();
            SameKeyGenerator privateKeyProvider = new SameKeyGenerator(privateKey);

            PingMessageSerializer pingSerializer = new PingMessageSerializer(ecdsa, privateKeyProvider, new DiscoveryMessageFactory(networkConfig, _timestamp), new NodeIdResolver(ecdsa));
            PongMessageSerializer pongSerializer = new PongMessageSerializer(ecdsa, privateKeyProvider, new DiscoveryMessageFactory(networkConfig, _timestamp), new NodeIdResolver(ecdsa));
            FindNodeMessageSerializer findNodeSerializer = new FindNodeMessageSerializer(ecdsa, privateKeyProvider, new DiscoveryMessageFactory(networkConfig, _timestamp), new NodeIdResolver(ecdsa));
            NeighborsMessageSerializer neighborsSerializer = new NeighborsMessageSerializer(ecdsa, privateKeyProvider, new DiscoveryMessageFactory(networkConfig, _timestamp), new NodeIdResolver(ecdsa));

            return With(pingSerializer)
                .With(pongSerializer)
                .With(findNodeSerializer)
                .With(neighborsSerializer);
        }
    }
}