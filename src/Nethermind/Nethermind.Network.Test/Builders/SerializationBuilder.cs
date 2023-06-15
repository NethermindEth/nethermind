// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Specs;

namespace Nethermind.Network.Test.Builders
{
    public class SerializationBuilder : BuilderBase<IMessageSerializationService>
    {
        private readonly ITimestamper _timestamper;

        public SerializationBuilder(ITimestamper timestamper = null)
        {
            _timestamper = timestamper ?? Timestamper.Default;
            TestObject = new MessageSerializationService();
        }

        public SerializationBuilder With<T>(IZeroMessageSerializer<T> serializer) where T : MessageBase
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
            return With(new Nethermind.Network.P2P.Messages.PingMessageSerializer())
                .With(new Nethermind.Network.P2P.Messages.PongMessageSerializer())
                .With(new Nethermind.Network.P2P.Messages.HelloMessageSerializer())
                .With(new Nethermind.Network.P2P.Messages.DisconnectMessageSerializer());
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

        public SerializationBuilder WithEth65()
        {
            return WithEth()
                .With(new NewPooledTransactionHashesMessageSerializer())
                .With(new GetPooledTransactionsMessageSerializer())
                .With(new PooledTransactionsMessageSerializer());
        }

        public SerializationBuilder WithEth66()
        {
            return WithEth65()
                .With(new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessageSerializer())
                .With(new Network.P2P.Subprotocols.Eth.V66.Messages.BlockHeadersMessageSerializer())
                .With(new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockBodiesMessageSerializer())
                .With(new Network.P2P.Subprotocols.Eth.V66.Messages.BlockBodiesMessageSerializer())
                .With(new Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessageSerializer())
                .With(new Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessageSerializer())
                .With(new Network.P2P.Subprotocols.Eth.V66.Messages.GetNodeDataMessageSerializer())
                .With(new Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessageSerializer())
                .With(new Network.P2P.Subprotocols.Eth.V66.Messages.GetReceiptsMessageSerializer())
                .With(new Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessageSerializer(new ReceiptsMessageSerializer(MainnetSpecProvider.Instance)));
        }

        public SerializationBuilder WithEth68()
        {
            return WithEth66()
                .With(new Network.P2P.Subprotocols.Eth.V68.Messages.NewPooledTransactionHashesMessageSerializer());
        }

        public SerializationBuilder WithNodeData()
        {
            return With(new Network.P2P.Subprotocols.NodeData.Messages.GetNodeDataMessageSerializer())
                .With(new Network.P2P.Subprotocols.NodeData.Messages.NodeDataMessageSerializer());
        }

        public SerializationBuilder WithDiscovery(PrivateKey privateKey)
        {
            Ecdsa ecdsa = new();
            SameKeyGenerator privateKeyProvider = new(privateKey);

            PingMsgSerializer pingSerializer = new(ecdsa, privateKeyProvider, new NodeIdResolver(ecdsa));
            PongMsgSerializer pongSerializer = new(ecdsa, privateKeyProvider, new NodeIdResolver(ecdsa));
            FindNodeMsgSerializer findNodeSerializer = new(ecdsa, privateKeyProvider, new NodeIdResolver(ecdsa));
            NeighborsMsgSerializer neighborsSerializer = new(ecdsa, privateKeyProvider, new NodeIdResolver(ecdsa));
            EnrRequestMsgSerializer enrRequestSerializer = new(ecdsa, privateKeyProvider, new NodeIdResolver(ecdsa));
            EnrResponseMsgSerializer enrResponseSerializer = new(ecdsa, privateKeyProvider, new NodeIdResolver(ecdsa));

            return With(pingSerializer)
                .With(pongSerializer)
                .With(findNodeSerializer)
                .With(neighborsSerializer)
                .With(enrRequestSerializer)
                .With(enrResponseSerializer);
        }
    }
}
