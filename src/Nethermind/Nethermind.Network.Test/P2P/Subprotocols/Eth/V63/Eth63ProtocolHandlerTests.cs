//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
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
            Context ctx = new Context();
            StatusMessageSerializer statusMessageSerializer = new StatusMessageSerializer();
            ReceiptsMessageSerializer receiptMessageSerializer
                = new ReceiptsMessageSerializer(MainnetSpecProvider.Instance);
            MessageSerializationService serializationService = new MessageSerializationService();
            serializationService.Register(statusMessageSerializer);
            serializationService.Register(receiptMessageSerializer);

            Eth63ProtocolHandler protocolHandler = new Eth63ProtocolHandler(
                ctx.Session,
                serializationService,
                Substitute.For<INodeStatsManager>(),
                Substitute.For<ISyncServer>(),
                Substitute.For<ITxPool>(),
                LimboLogs.Instance);

            var receipts = Enumerable.Repeat(
                Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, 100).ToArray(),
                1000).ToArray(); // TxReceipt[1000][100]

            StatusMessage statusMessage = new StatusMessage();
            Packet statusPacket =
                new Packet("eth", Eth62MessageCode.Status, statusMessageSerializer.Serialize(statusMessage));

            ReceiptsMessage receiptsMsg = new ReceiptsMessage(receipts);
            Packet receiptsPacket =
                new Packet("eth", Eth63MessageCode.Receipts, receiptMessageSerializer.Serialize(receiptsMsg));

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
            Context ctx = new Context();
            StatusMessageSerializer statusMessageSerializer = new StatusMessageSerializer();
            ReceiptsMessageSerializer receiptMessageSerializer
                = new ReceiptsMessageSerializer(MainnetSpecProvider.Instance);
            GetReceiptsMessageSerializer getReceiptMessageSerializer
                = new GetReceiptsMessageSerializer();
            MessageSerializationService serializationService = new MessageSerializationService();
            serializationService.Register(statusMessageSerializer);
            serializationService.Register(receiptMessageSerializer);
            serializationService.Register(getReceiptMessageSerializer);
            
            ISyncServer syncServer = Substitute.For<ISyncServer>();
            Eth63ProtocolHandler protocolHandler = new Eth63ProtocolHandler(
                ctx.Session,
                serializationService,
                Substitute.For<INodeStatsManager>(),
                syncServer,
                Substitute.For<ITxPool>(),
                LimboLogs.Instance);

            StatusMessage statusMessage = new StatusMessage();
            Packet statusPacket =
                new Packet("eth", Eth62MessageCode.Status, statusMessageSerializer.Serialize(statusMessage));

            GetReceiptsMessage getReceiptsMessage = new GetReceiptsMessage(
                Enumerable.Repeat(Keccak.Zero, 513).ToArray());
            Packet getReceiptsPacket =
                new Packet("eth", Eth63MessageCode.GetReceipts, getReceiptMessageSerializer.Serialize(getReceiptsMessage));

            protocolHandler.HandleMessage(statusPacket);
            Assert.Throws<EthSyncException>(() =>protocolHandler.HandleMessage(getReceiptsPacket));
        }

        [Test]
        public void Will_not_send_messages_larger_than_2MB()
        {
            Context ctx = new Context();
            StatusMessageSerializer statusMessageSerializer = new StatusMessageSerializer();
            ReceiptsMessageSerializer receiptMessageSerializer
                = new ReceiptsMessageSerializer(MainnetSpecProvider.Instance);
            GetReceiptsMessageSerializer getReceiptMessageSerializer
                = new GetReceiptsMessageSerializer();
            MessageSerializationService serializationService = new MessageSerializationService();
            serializationService.Register(statusMessageSerializer);
            serializationService.Register(receiptMessageSerializer);
            serializationService.Register(getReceiptMessageSerializer);
            
            ISyncServer syncServer = Substitute.For<ISyncServer>();
            Eth63ProtocolHandler protocolHandler = new Eth63ProtocolHandler(
                ctx.Session,
                serializationService,
                Substitute.For<INodeStatsManager>(),
                syncServer,
                Substitute.For<ITxPool>(),
                LimboLogs.Instance);

            syncServer.GetReceipts(Arg.Any<Keccak>()).Returns(
                Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, 512).ToArray());

            StatusMessage statusMessage = new StatusMessage();
            Packet statusPacket =
                new Packet("eth", Eth62MessageCode.Status, statusMessageSerializer.Serialize(statusMessage));

            GetReceiptsMessage getReceiptsMessage = new GetReceiptsMessage(
                Enumerable.Repeat(Keccak.Zero, 512).ToArray());
            Packet getReceiptsPacket =
                new Packet("eth", Eth63MessageCode.GetReceipts, getReceiptMessageSerializer.Serialize(getReceiptsMessage));

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
                Session.Node.Returns(new Node("127.0.0.1", 1000, true));
                NetworkDiagTracer.IsEnabled = true;
            }
        }
    }
}
