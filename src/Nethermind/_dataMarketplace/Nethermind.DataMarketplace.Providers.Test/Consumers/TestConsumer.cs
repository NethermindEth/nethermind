using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Peers;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Nethermind.DataMarketplace.Providers.Test.Consumers
{
    internal class TestConsumer
    {
        private static readonly PrivateKeyGenerator PrivateKeyGenerator = new PrivateKeyGenerator();
        private readonly ISet<TestConsumerNode> _nodes = new HashSet<TestConsumerNode>();
        public Keccak DepositId { get; private set; }
        public DataAsset DataAsset => Consumer.DataAsset;
        public DataRequest DataRequest => Consumer.DataRequest;
        public Consumer Consumer { get; private set; }
        public IEnumerable<TestConsumerNode> Nodes => _nodes;
        public IEnumerable<ProviderSession> Sessions => Nodes.SelectMany(n => n.Sessions);

        public TestConsumerNode Node(int id) => Nodes.SingleOrDefault(n => n.Id == id);
        
        public void AddReceipts(params DataDeliveryReceiptDetails[] receipts)
        {
            if (receipts is null)
            {
                return;
            }

            foreach (var receipt in receipts)
            {
                var node = Nodes.SingleOrDefault(n => n.Node.Peer.NodeId == receipt.ConsumerNodeId);
                node?.AddReceipts(receipt);
            }
        }

        private TestConsumer()
        {
        }

        public static Builder ForDeposit(Keccak depositId, DataAssetUnitType unitType = DataAssetUnitType.Unit) => new Builder(depositId, unitType);

        internal class Builder
        {
            private readonly TestConsumer _consumer = new TestConsumer();
            private readonly Keccak _depositId;

            protected Builder()
            {
            }

            public Builder(Keccak depositId, DataAssetUnitType unitType)
            {
                _depositId = depositId;
                var dataAsset = unitType == DataAssetUnitType.Unit ? CreateDataAsset() : CreateTimeDataAsset();
                var dataRequest = CreateDataRequest(dataAsset.Id);
                _consumer.DepositId = depositId;
                _consumer.Consumer = new Consumer(depositId, 0, dataRequest, dataAsset);
            }

            public NodeBuilder WithNode(int id)
            {
                var peer = Substitute.For<INdmProviderPeer>();
                peer.NodeId.Returns(PrivateKeyGenerator.Generate().PublicKey);
                var node = new TestConsumerNode(id, new ConsumerNode(peer));
                _consumer._nodes.Add(node);

                return new NodeBuilder(this, _depositId, node);
            }

            public TestConsumer Build() => _consumer;

            private static DataRequest CreateDataRequest(Keccak dataAssetId)
                => new DataRequest(dataAssetId, 1000, 1000, 1, Array.Empty<byte>(), Address.Zero, Address.Zero, new Signature(1, 2 , 37));

            private static DataAsset CreateDataAsset()
                => new DataAsset(Keccak.OfAnEmptyString, "test", "test", 1, DataAssetUnitType.Unit,
                    1, 100000, new DataAssetRules(new DataAssetRule(1)),
                    new DataAssetProvider(Address.Zero, "test"));

            private static DataAsset CreateTimeDataAsset()
            {
                return new DataAsset(
                    Keccak.OfAnEmptySequenceRlp,
                    "time-asset",
                    "time test",
                    1,
                    DataAssetUnitType.Time,
                    1, 
                    10000, 
                    new DataAssetRules(new DataAssetRule(1)), 
                    new DataAssetProvider(Address.Zero, "test"));
            }
        }

        internal class NodeBuilder
        {
            private readonly Keccak _depositId;
            private readonly TestConsumerNode _node;
            public Builder And { get; }

            public NodeBuilder(Builder builder, Keccak depositId, TestConsumerNode node)
            {
                And = builder;
                _depositId = depositId;
                _node = node;
                _node.Node.Peer.SendRequestDataDeliveryReceiptAsync(Arg.Any<DataDeliveryReceiptRequest>(),
                    Arg.Any<CancellationToken>()).Returns(new DataDeliveryReceipt(StatusCodes.Ok, 0, 0, null));
            }

            public NodeBuilder WillNotDeliverReceipt()
            {
                _node.Node.Peer.SendRequestDataDeliveryReceiptAsync(Arg.Any<DataDeliveryReceiptRequest>(),
                    Arg.Any<CancellationToken>()).Throws(new Exception());

                return this;
            }

            public SessionBuilder AddSession()
            {
                var session = CreateSession(_depositId);
                session.Start(0);
                _node.AddSession(session);

                return new SessionBuilder(session, this, And);
            }

            private static ProviderSession CreateSession(Keccak depositId)
                => new ProviderSession(Keccak.Compute(Guid.NewGuid().ToString("N")), depositId, Keccak.Zero,
                    Address.Zero, null, Address.Zero, null, 0, 0);
        }

        internal class SessionBuilder
        {
            private readonly ProviderSession _session;
            public NodeBuilder Node { get; }
            public Builder And { get; }

            public SessionBuilder(ProviderSession session, NodeBuilder nodeBuilder, Builder builder)
            {
                _session = session;
                Node = nodeBuilder;
                And = builder;
            }
            
            public SessionBuilder WithUnpaidUnits(uint unpaidUnits)
            {
                _session.SetUnpaidUnits(unpaidUnits);
                
                return this;
            }

            public SessionBuilder WithConsumedUnits(uint consumedUnits)
            {
                _session.SetConsumedUnits(consumedUnits);

                return this;
            }
        }
    }
}