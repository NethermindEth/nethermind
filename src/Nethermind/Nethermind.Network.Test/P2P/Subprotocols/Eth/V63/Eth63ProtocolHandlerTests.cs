// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class Eth63ProtocolHandlerTests
    {
        [Test]
        public async Task Can_request_and_handle_receipts()
        {
            Context ctx = new();
            StatusMessageSerializer statusMessageSerializer = new();
            ReceiptsMessageSerializer receiptMessageSerializer
                = new(MainnetSpecProvider.Instance);
            MessageSerializationService serializationService = new();
            serializationService.Register(statusMessageSerializer);
            serializationService.Register(receiptMessageSerializer);

            Eth63ProtocolHandler protocolHandler = new(
                ctx.Session,
                serializationService,
                Substitute.For<INodeStatsManager>(),
                Substitute.For<ISyncServer>(),
                Substitute.For<ITxPool>(),
                Substitute.For<IGossipPolicy>(),
                LimboLogs.Instance);

            var receipts = Enumerable.Repeat(
                Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, 100).ToArray(),
                1000).ToArray(); // TxReceipt[1000][100]

            StatusMessage statusMessage = new();
            Packet statusPacket =
                new("eth", Eth62MessageCode.Status, statusMessageSerializer.Serialize(statusMessage));

            ReceiptsMessage receiptsMsg = new(receipts);
            Packet receiptsPacket =
                new("eth", Eth63MessageCode.Receipts, receiptMessageSerializer.Serialize(receiptsMsg));

            protocolHandler.HandleMessage(statusPacket);
            Task<TxReceipt[][]> task = protocolHandler.GetReceipts(
                Enumerable.Repeat(Keccak.Zero, 1000).ToArray(),
                CancellationToken.None);

            protocolHandler.HandleMessage(receiptsPacket);

            var result = await task;
            result.Should().HaveCount(1000);
        }

        [Test]
        public void Will_not_serve_receipts_requests_above_512()
        {
            Context ctx = new();
            StatusMessageSerializer statusMessageSerializer = new();
            ReceiptsMessageSerializer receiptMessageSerializer
                = new(MainnetSpecProvider.Instance);
            GetReceiptsMessageSerializer getReceiptMessageSerializer
                = new();
            MessageSerializationService serializationService = new();
            serializationService.Register(statusMessageSerializer);
            serializationService.Register(receiptMessageSerializer);
            serializationService.Register(getReceiptMessageSerializer);

            ISyncServer syncServer = Substitute.For<ISyncServer>();
            Eth63ProtocolHandler protocolHandler = new(
                ctx.Session,
                serializationService,
                Substitute.For<INodeStatsManager>(),
                syncServer,
                Substitute.For<ITxPool>(),
                Substitute.For<IGossipPolicy>(),
                LimboLogs.Instance);

            StatusMessage statusMessage = new();
            Packet statusPacket =
                new("eth", Eth62MessageCode.Status, statusMessageSerializer.Serialize(statusMessage));

            GetReceiptsMessage getReceiptsMessage = new(
                Enumerable.Repeat(Keccak.Zero, 513).ToArray());
            Packet getReceiptsPacket =
                new("eth", Eth63MessageCode.GetReceipts, getReceiptMessageSerializer.Serialize(getReceiptsMessage));

            protocolHandler.HandleMessage(statusPacket);
            Assert.Throws<EthSyncException>(() => protocolHandler.HandleMessage(getReceiptsPacket));
        }

        [Test]
        public void Will_not_send_messages_larger_than_2MB()
        {
            Context ctx = new();
            StatusMessageSerializer statusMessageSerializer = new();
            ReceiptsMessageSerializer receiptMessageSerializer
                = new(MainnetSpecProvider.Instance);
            GetReceiptsMessageSerializer getReceiptMessageSerializer
                = new();
            MessageSerializationService serializationService = new();
            serializationService.Register(statusMessageSerializer);
            serializationService.Register(receiptMessageSerializer);
            serializationService.Register(getReceiptMessageSerializer);

            ISyncServer syncServer = Substitute.For<ISyncServer>();
            Eth63ProtocolHandler protocolHandler = new(
                ctx.Session,
                serializationService,
                Substitute.For<INodeStatsManager>(),
                syncServer,
                Substitute.For<ITxPool>(),
                Substitute.For<IGossipPolicy>(),
                LimboLogs.Instance);

            syncServer.GetReceipts(Arg.Any<Keccak>()).Returns(
                Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, 512).ToArray());

            StatusMessage statusMessage = new();
            Packet statusPacket =
                new("eth", Eth62MessageCode.Status, statusMessageSerializer.Serialize(statusMessage));

            GetReceiptsMessage getReceiptsMessage = new(
                Enumerable.Repeat(Keccak.Zero, 512).ToArray());
            Packet getReceiptsPacket =
                new("eth", Eth63MessageCode.GetReceipts, getReceiptMessageSerializer.Serialize(getReceiptsMessage));

            protocolHandler.HandleMessage(statusPacket);
            protocolHandler.HandleMessage(getReceiptsPacket);
            ctx.Session.Received().DeliverMessage(Arg.Is<ReceiptsMessage>(r => r.TxReceipts.Length == 14));
        }

        private class Context
        {
            public ISession Session { get; set; }

            public Context()
            {
                Session = Substitute.For<ISession>();
                Session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 1000, true));
                NetworkDiagTracer.IsEnabled = true;
            }
        }
    }
}
