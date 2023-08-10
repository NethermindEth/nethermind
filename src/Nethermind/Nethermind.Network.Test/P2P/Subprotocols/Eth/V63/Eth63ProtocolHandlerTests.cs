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
    [TestFixture, Parallelizable(ParallelScope.Fixtures)]
    public class Eth63ProtocolHandlerTests
    {
        [Test]
        public async Task Can_request_and_handle_receipts()
        {
            Context ctx = new();
            TxReceipt[][]? receipts = Enumerable.Repeat(
                Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, 100).ToArray(),
                1000).ToArray(); // TxReceipt[1000][100]

            ReceiptsMessage receiptsMsg = new(receipts);
            Packet receiptsPacket =
                new("eth", Eth63MessageCode.Receipts, ctx._receiptMessageSerializer.Serialize(receiptsMsg));

            Task<TxReceipt[][]> task = ctx.ProtocolHandler.GetReceipts(
                Enumerable.Repeat(Keccak.Zero, 1000).ToArray(),
                CancellationToken.None);

            ctx.ProtocolHandler.HandleMessage(receiptsPacket);

            var result = await task;
            result.Should().HaveCount(1000);
        }

        [Test]
        public async Task Limit_receipt_request()
        {
            Context ctx = new();
            TxReceipt[] oneBlockReceipt = Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, 100).ToArray();
            Packet smallReceiptsPacket =
                new("eth", Eth63MessageCode.Receipts, ctx._receiptMessageSerializer.Serialize(
                    new(Enumerable.Repeat(oneBlockReceipt, 10).ToArray())
                ));
            Packet largeReceiptsPacket =
                new("eth", Eth63MessageCode.Receipts, ctx._receiptMessageSerializer.Serialize(
                    new(Enumerable.Repeat(oneBlockReceipt, 1000).ToArray())
                ));

            GetReceiptsMessage? receiptsMessage = null;

            ctx.Session
                .When(session => session.DeliverMessage(Arg.Any<GetReceiptsMessage>()))
                .Do((info => receiptsMessage = (GetReceiptsMessage)info[0]));

            Task<TxReceipt[][]> receiptsTask = ctx.ProtocolHandler.GetReceipts(
                Enumerable.Repeat(Keccak.Zero, 1000).ToArray(),
                CancellationToken.None);

            ctx.ProtocolHandler.HandleMessage(smallReceiptsPacket);
            await receiptsTask;

            Assert.That(receiptsMessage?.Hashes?.Count, Is.EqualTo(8));

            receiptsTask = ctx.ProtocolHandler.GetReceipts(
                Enumerable.Repeat(Keccak.Zero, 1000).ToArray(),
                CancellationToken.None);

            ctx.ProtocolHandler.HandleMessage(largeReceiptsPacket);
            await receiptsTask;

            Assert.That(receiptsMessage?.Hashes?.Count, Is.EqualTo(12));

            // Back to 10
            receiptsTask = ctx.ProtocolHandler.GetReceipts(
                Enumerable.Repeat(Keccak.Zero, 1000).ToArray(),
                CancellationToken.None);

            ctx.ProtocolHandler.HandleMessage(smallReceiptsPacket);
            await receiptsTask;

            Assert.That(receiptsMessage?.Hashes?.Count, Is.EqualTo(8));
        }

        [Test]
        public void Will_not_serve_receipts_requests_above_512()
        {
            Context ctx = new();
            GetReceiptsMessage getReceiptsMessage = new(
                Enumerable.Repeat(Keccak.Zero, 513).ToArray());
            Packet getReceiptsPacket =
                new("eth", Eth63MessageCode.GetReceipts, ctx._getReceiptMessageSerializer.Serialize(getReceiptsMessage));

            Assert.Throws<EthSyncException>(() => ctx.ProtocolHandler.HandleMessage(getReceiptsPacket));
        }

        [Test]
        public void Will_not_send_messages_larger_than_2MB()
        {
            Context ctx = new();
            ctx.SyncServer.GetReceipts(Arg.Any<Keccak>()).Returns(
                Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, 512).ToArray());

            GetReceiptsMessage getReceiptsMessage = new(
                Enumerable.Repeat(Keccak.Zero, 512).ToArray());
            Packet getReceiptsPacket =
                new("eth", Eth63MessageCode.GetReceipts, ctx._getReceiptMessageSerializer.Serialize(getReceiptsMessage));

            ctx.ProtocolHandler.HandleMessage(getReceiptsPacket);
            ctx.Session.Received().DeliverMessage(Arg.Is<ReceiptsMessage>(r => r.TxReceipts.Length == 14));
        }

        private class Context
        {
            readonly MessageSerializationService _serializationService = new();
            private readonly StatusMessageSerializer _statusMessageSerializer = new();
            public readonly ReceiptsMessageSerializer _receiptMessageSerializer = new(MainnetSpecProvider.Instance);
            public readonly GetReceiptsMessageSerializer _getReceiptMessageSerializer = new();

            public ISession Session { get; }

            private ISyncServer? _syncServer;
            public ISyncServer SyncServer => _syncServer ??= Substitute.For<ISyncServer>();

            private Eth63ProtocolHandler? _protocolHandler;
            public Eth63ProtocolHandler ProtocolHandler
            {
                get
                {
                    if (_protocolHandler != null)
                    {
                        return _protocolHandler;
                    }

                    _protocolHandler = new Eth63ProtocolHandler(
                        Session,
                        _serializationService,
                        Substitute.For<INodeStatsManager>(),
                        SyncServer,
                        Substitute.For<ITxPool>(),
                        Substitute.For<IGossipPolicy>(),
                        LimboLogs.Instance);

                    StatusMessage statusMessage = new();
                    Packet statusPacket =
                        new("eth", Eth62MessageCode.Status, _statusMessageSerializer.Serialize(statusMessage));
                    _protocolHandler.HandleMessage(statusPacket);

                    return _protocolHandler;
                }
            }

            public Context()
            {
                Session = Substitute.For<ISession>();
                Session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 1000, true));
                NetworkDiagTracer.IsEnabled = true;

                _serializationService.Register(_statusMessageSerializer);
                _serializationService.Register(_receiptMessageSerializer);
                _serializationService.Register(_getReceiptMessageSerializer);
            }
        }
    }
}
